package opa

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"strconv"

	"github.com/azure/azure-cleanroom/src/internal/filter"
	corev3 "github.com/envoyproxy/go-control-plane/envoy/config/core/v3"
	pb "github.com/envoyproxy/go-control-plane/envoy/service/ext_proc/v3"
	typev3 "github.com/envoyproxy/go-control-plane/envoy/type/v3"
	"github.com/open-policy-agent/opa/v1/rego"
	"github.com/open-policy-agent/opa/v1/topdown"
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
func (f *opaFilter) OnRequestHeaders(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	headers := req.Request.(*pb.ProcessingRequest_RequestHeaders)
	f.method = filter.ExtractHeader(filter.Method, headers)
	f.path = filter.ExtractHeader(filter.Path, headers)

	span := trace.SpanFromContext(ctx)
	span.SetAttributes(
		attribute.String("request.path", f.path),
		attribute.String("request.method", f.method),
	)
	_, response := f.processRequest(ctx, rule_OnRequestHeaders, req)
	if response != nil {
		return response
	}

	return filter.CreateRequestHeadersProxyResponse(pb.CommonResponse_CONTINUE, nil, nil)
}

// Processes the specified confidential request body.
func (f *opaFilter) OnRequestBody(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	body := req.Request.(*pb.ProcessingRequest_RequestBody)
	log.Debugf("Handling confidential %s '%s' request", f.method, f.path)
	evalResult, response := f.processRequest(ctx, rule_OnRequestBody, req)
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
func (f *opaFilter) OnResponseHeaders(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	_, response := f.processRequest(ctx, rule_OnResponseHeaders, req)
	if response != nil {
		return response
	}

	return filter.CreateResponseHeadersProxyResponse(pb.CommonResponse_CONTINUE, nil, nil)
}

// Processes the specified confidential response body.
func (f *opaFilter) OnResponseBody(ctx context.Context, req *pb.ProcessingRequest) *pb.ProcessingResponse {
	body := req.Request.(*pb.ProcessingRequest_ResponseBody)
	log.Debugf("Handling confidential %s '%s' response", f.method, f.path)
	evalResult, response := f.processRequest(ctx, rule_OnResponseBody, req)
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

func (f *opaFilter) processRequest(
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

	log.Infof("Evaluating '%s' policy for %s '%s'", rule, f.method, f.path)
	input["context"] = f.currentRequestContext
	input["teeType"] = f.teeType
	result, err := f.eval(rule, input)
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
		f.currentRequestContext = context
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

func (f *opaFilter) eval(rule rule, input interface{}) (evalResult, error) {
	ctx := context.TODO()
	result := evalResult{}
	var pb bytes.Buffer
	ph := topdown.NewPrintHook(&pb)
	nt := newNoteQueryTracer()
	query := f.policyQueries[rule]
	resultSet, err :=
		query.Eval(ctx, rego.EvalInput(input), rego.EvalPrintHook(ph), rego.EvalQueryTracer(nt))
	printStatements := pb.String()
	if printStatements != "" {
		log.Infof("'%s' policy print output:\n%s", rule, pb.String())
	}

	var tb bytes.Buffer
	topdown.PrettyTraceWithLocation(&tb, *nt.bt)
	if tb.Len() > 0 {
		log.Infof("'%s' policy trace:\n%s", rule, tb.String())
	}

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

	span := trace.SpanFromContext(ctx)
	span.AddEvent("policy.denied", trace.WithAttributes(
		attribute.Int("response.status", int(httpStatus)),
	))
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
