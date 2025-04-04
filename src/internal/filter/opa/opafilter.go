package opa

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strconv"

	"github.com/azure/azure-cleanroom/src/internal/filter"
	corev3 "github.com/envoyproxy/go-control-plane/envoy/config/core/v3"
	pb "github.com/envoyproxy/go-control-plane/envoy/service/ext_proc/v3"
	typev3 "github.com/envoyproxy/go-control-plane/envoy/type/v3"
	"github.com/open-policy-agent/opa/rego"
	"github.com/open-policy-agent/opa/topdown"
	log "github.com/sirupsen/logrus"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/trace"
	"google.golang.org/protobuf/encoding/protojson"
)

type opaFilter struct {
	policyQueries         map[rule]rego.PreparedEvalQuery
	currentRequestContext interface{}
	method                string
	path                  string
	teeType               string
	tracer                trace.Tracer
}

// Processes the specified confidential request headers.
func (self *opaFilter) OnRequestHeaders(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	headers := req.Request.(*pb.ProcessingRequest_RequestHeaders)
	self.method = filter.ExtractHeader(filter.Method, headers)
	self.path = filter.ExtractHeader(filter.Path, headers)

	span := trace.SpanFromContext(ctx)
	span.SetAttributes(
		attribute.String("request.path", self.path),
		attribute.String("request.method", self.method),
	)
	_, response := self.processRequest(ctx, rule_OnRequestHeaders, req)
	if response != nil {
		return response
	}

	return filter.CreateRequestHeadersProxyResponse(pb.CommonResponse_CONTINUE, nil, nil)
}

// Processes the specified confidential request body.
func (self *opaFilter) OnRequestBody(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	body := req.Request.(*pb.ProcessingRequest_RequestBody)
	log.Debugf("Handling confidential %s '%s' request", self.method, self.path)
	evalResult, response := self.processRequest(ctx, rule_OnRequestBody, req)
	if response != nil {
		return response
	}

	var err error
	defer filter.RecordSpanError(ctx, &err)

	headerMutation, bodyMutation, err := mutationResponse(evalResult, body.RequestBody)
	if err != nil {
		log.Errorf("failed to get mutation response: %s", err)
		return filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get mutation response")
	}

	return filter.CreateRequestBodyProxyResponse(
		pb.CommonResponse_CONTINUE, headerMutation, bodyMutation)
}

// Processes the specified confidential response headers.
func (self *opaFilter) OnResponseHeaders(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	_, response := self.processRequest(ctx, rule_OnResponseHeaders, req)
	if response != nil {
		return response
	}

	return filter.CreateResponseHeadersProxyResponse(pb.CommonResponse_CONTINUE, nil, nil)
}

// Processes the specified confidential response body.
func (self *opaFilter) OnResponseBody(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	body := req.Request.(*pb.ProcessingRequest_ResponseBody)
	log.Debugf("Handling confidential %s '%s' response", self.method, self.path)
	evalResult, response := self.processRequest(ctx, rule_OnResponseBody, req)
	if response != nil {
		return response
	}

	var err error
	defer filter.RecordSpanError(ctx, &err)

	headerMutation, bodyMutation, err := mutationResponse(evalResult, body.ResponseBody)
	if err != nil {
		log.Errorf("failed to get mutation response: %s", err)
		return filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get mutation response")
	}

	return filter.CreateResponseBodyProxyResponse(
		pb.CommonResponse_CONTINUE, headerMutation, bodyMutation)
}

func (self *opaFilter) processRequest(
	ctx context.Context,
	rule rule,
	req *pb.ProcessingRequest) (*evalResult, *pb.ProcessingResponse) {
	var err error
	defer filter.RecordSpanError(ctx, &err)

	input, err := requestToInput(req)
	if err != nil {
		log.Errorf("failed to convert incoming message to policy input: %s", err)
		return nil, filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to convert incoming message to policy input")
	}

	log.Infof("Evaluating '%s' policy for %s '%s'", rule, self.method, self.path)
	input["context"] = self.currentRequestContext
	input["teeType"] = self.teeType
	result, err := self.eval(rule, input)
	if err != nil {
		log.Errorf("failed to evaluate query: %s", err)
		return nil, filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to evaluate query")
	}

	allowed, err := result.IsAllowed()
	if err != nil {
		log.Errorf("IsAllowed invocation failed: %s", err)
		return nil, filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get allowed value")
	}

	if !allowed {
		return nil, disallowedResponse(ctx, result)
	}

	isImmediateResponse, err := result.IsImmediateResponse()
	if err != nil {
		log.Errorf("failed to get response body: %s", err)
		return nil, filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get response body")
	}

	if isImmediateResponse {
		return nil, immediateResponse(ctx, result)
	}

	context, err := result.GetResponseContext()
	if err != nil {
		log.Errorf("failed to get response context: %s", err)
		return nil, filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get response context")
	}

	if context != nil {
		self.currentRequestContext = context
	}

	return &result, nil
}

// The JSON mapping of the protobuf message is used for making the entire incoming
// envoy.service.ext_proc.v3.ProcessingRequest available in input for policy evaluation.
func requestToInput(req *pb.ProcessingRequest) (map[string]interface{}, error) {
	bs, err := protojson.Marshal(req)
	if err != nil {
		log.Errorf("failed to Marshal protobuf message: %s", err)
		return nil, err
	}

	log.Debugf("Query input: %s", string(bs))

	var input map[string]interface{}
	err = json.Unmarshal(bs, &input)
	if err != nil {
		log.Errorf("failed to Unmarshal protobuf JSON: %s", err)
		return nil, err
	}

	return input, nil
}

func (self *opaFilter) eval(rule rule, input interface{}) (evalResult, error) {
	ctx := context.TODO()
	result := evalResult{}
	var pb bytes.Buffer
	ph := topdown.NewPrintHook(&pb)
	nt := newNoteQueryTracer()
	query := self.policyQueries[rule]
	resultSet, err :=
		query.Eval(ctx, rego.EvalInput(input), rego.EvalPrintHook(ph), rego.EvalQueryTracer(nt))
	printStatements := pb.String()
	if printStatements != "" {
		log.Infof("'%s' policy print output:\n%s", rule, pb.String())
	}

	// TODO (gsinha): Hook this output via logrus and not stdout.
	topdown.PrettyTraceWithLocation(os.Stdout, *nt.bt)

	switch {
	case err != nil:
		log.Errorf("failed to run query: %s", err)
		return result, err

	case len(resultSet) == 0:
		// Handle undefined result.
		log.Errorf("got undefined result on running query")
		return result, fmt.Errorf("got undefined result on running query")

	case len(resultSet) > 1:
		// Handle undefined result.
		log.Errorf("got multiple evaluation results on running query")
		return result, fmt.Errorf("got multiple evaluation results on running query")
	}

	decision := resultSet[0].Expressions[0].Value
	log.Infof("Got result/decision: %v", decision)
	result = NewEvalResult(decision)
	return result, nil
}

func disallowedResponse(ctx context.Context, evalResult evalResult) *pb.ProcessingResponse {
	var err error
	defer filter.RecordSpanError(ctx, &err)

	body, err := evalResult.GetResponseBody()
	if err != nil {
		log.Errorf("failed to get response body: %s", err)
		return filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get response body")
	}

	httpStatus, err := evalResult.GetResponseEnvoyHTTPStatus()
	if err != nil {
		log.Errorf("failed to get response status: %s", err)
		return filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get response status")
	}

	// TODO (gsinha): Revisit whether a logical disallowed response should be treated as an error
	// for the span. This might not be an "issue" that the infra sidecars need to monitor but the
	// business logic of the policy bundle which is opaque to us.
	disallowedError := fmt.Errorf("%s", body)
	filter.RecordSpanError(ctx, &disallowedError)
	return filter.CreateImmediateProxyResponse(
		httpStatus,
		body,
		"disallowed policy decision response")
}

func immediateResponse(ctx context.Context, evalResult evalResult) *pb.ProcessingResponse {
	var err error
	defer filter.RecordSpanError(ctx, &err)

	body, err := evalResult.GetResponseBody()
	if err != nil {
		log.Errorf("failed to get response body: %s", err)
		return filter.CreateErrorProxyResponse(
			typev3.StatusCode_InternalServerError,
			"failed to get response body")
	}

	return filter.CreateImmediateProxyResponse(
		typev3.StatusCode_OK,
		body,
		"allowed immediate policy decision response")
}

func mutationResponse(evalResult *evalResult, body *pb.HttpBody) (
	*pb.HeaderMutation, *pb.BodyMutation, error) {
	// Check whether body has been mutated and if so send a body mutation response.
	responseBody, err := evalResult.GetResponseBody()
	if err != nil {
		log.Errorf("failed to get response body: %s", err)
		return nil, nil, err
	}

	if responseBody != "" {
		processedBodyResult := []byte(responseBody)
		if !bytes.Equal(body.Body, processedBodyResult) {
			// The request body has been mutated, so return a body mutation response.
			headerMutation := &pb.HeaderMutation{
				SetHeaders: []*corev3.HeaderValueOption{
					{
						Header: &corev3.HeaderValue{
							Key:   "Content-Length",
							Value: strconv.Itoa(len(processedBodyResult)),
						},
					},
				},
			}
			bodyMutation := &pb.BodyMutation{
				Mutation: &pb.BodyMutation_Body{
					Body: processedBodyResult,
				},
			}

			return headerMutation, bodyMutation, nil
		}
	}

	return nil, nil, nil
}
