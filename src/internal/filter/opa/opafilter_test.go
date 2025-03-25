package opa

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"strconv"
	"strings"
	"testing"

	"github.com/azure/azure-cleanroom/src/internal/configuration"
	"github.com/azure/azure-cleanroom/src/internal/filter"
	ext_proc "github.com/envoyproxy/go-control-plane/envoy/service/ext_proc/v3"
	typev3 "github.com/envoyproxy/go-control-plane/envoy/type/v3"
	"github.com/stretchr/testify/require"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
	"go.opentelemetry.io/otel/trace"
	"google.golang.org/protobuf/encoding/protojson"
)

const exampleRequestHeaders = `{
	"requestHeaders": {
        "headers": {
            "headers": [
                {
                    "key": ":path",
                    "raw_value": "%s"
                },
                {
                    "key": ":method",
                    "raw_value": "%s"
                },
                {
                    "key": "x-ccr-request-direction",
                    "raw_value": "%s"
                }
            ]
        },
        "endOfStream": true
    }
  }`

const exampleMutationRequestBody = `{
	"requestBody": {
        "body": "aW5wdXQgYm9keQ==",
        "endOfStream": true
    }
  }`

const exampleMutationResponseBody = `{
	"responseBody": {
        "body": "aW5wdXQgYm9keQ==",
        "endOfStream": true
    }
  }`

func Test_RequestHeader_PathAllowed(t *testing.T) {
	exp := tracetest.NewInMemoryExporter()
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithSyncer(exp),
	)
	tracer := tp.Tracer("tracer")

	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action1")),
		base64.StdEncoding.EncodeToString([]byte("GET")),
		base64.StdEncoding.EncodeToString([]byte("inbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	ctx, span := tracer.Start(context.Background(), "testspan")
	resp := f.OnRequestHeaders(ctx, &req)
	var ok bool
	hr, ok := resp.Response.(*ext_proc.ProcessingResponse_RequestHeaders)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	if hr.RequestHeaders.Response.Status != ext_proc.CommonResponse_CONTINUE {
		t.Fatal("Expected request to be allowed but got:", resp)
	}

	// Validate expected span data.
	span.End()
	spanStubs := exp.GetSpans()
	require.Len(t, spanStubs, 1)
	spanStub := spanStubs[0]
	require.Equal(t, 0, spanStub.ChildSpanCount)
	var foundPath bool
	var foundMethod bool
	for _, v := range spanStub.Attributes {
		if v.Key == "request.path" && v.Value.AsString() == "/api/action1" {
			foundPath = true
		}

		if v.Key == "request.method" && v.Value.AsString() == "GET" {
			foundMethod = true
		}
	}

	require.True(t, foundPath, "did not find expected request.path attribute: %v", spanStub.Attributes)
	require.True(t, foundMethod, "did not find expected request.method attribute: %v", spanStub.Attributes)
}

func Test_RequestHeader_PathDisallowed(t *testing.T) {
	exp := tracetest.NewInMemoryExporter()
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithSyncer(exp),
	)
	tracer := tp.Tracer("tracer")

	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action2")),
		base64.StdEncoding.EncodeToString([]byte("GET")),
		base64.StdEncoding.EncodeToString([]byte("inbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	ctx, span := tracer.Start(context.Background(), "testspan")
	resp := f.OnRequestHeaders(ctx, &req)
	var ok bool
	ir, ok := resp.Response.(*ext_proc.ProcessingResponse_ImmediateResponse)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", ir, resp)
	}

	expectedCode := typev3.StatusCode_Forbidden
	if ir.ImmediateResponse.Status.Code != expectedCode {
		t.Fatalf("Expected status code %v but got %v", expectedCode, ir.ImmediateResponse.Status.Code)
	}

	// Validate expected span data.
	span.End()
	spanStubs := exp.GetSpans()
	require.Len(t, spanStubs, 1)
	spanStub := spanStubs[0]
	var foundException bool
	var foundExceptionType bool
	var foundExceptionMessage bool
	for _, event := range spanStub.Events {
		if event.Name == "exception" {
			foundException = true
			for _, eventAttribute := range event.Attributes {
				if eventAttribute.Key == "exception.type" &&
					eventAttribute.Value.AsString() == "*errors.errorString" {
					foundExceptionType = true
				}

				if eventAttribute.Key == "exception.message" &&
					eventAttribute.Value.AsString() == "RequestNotAllowed" {
					foundExceptionMessage = true
				}

				if foundExceptionType && foundExceptionMessage {
					break
				}
			}

			require.True(t,
				foundExceptionType,
				"did not find expected exception.type attribute: %v",
				event.Attributes)
			require.True(t,
				foundExceptionMessage,
				"did not find expected exception.message attribute: %v",
				event.Attributes)
			break
		}
	}

	require.True(t, foundException, "did not find expected exception event: %v", spanStub.Events)
}

func Test_RequestHeader_HeaderDisallowed(t *testing.T) {
	exp := tracetest.NewInMemoryExporter()
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithSyncer(exp),
	)
	tracer := tp.Tracer("tracer")

	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action1")),
		base64.StdEncoding.EncodeToString([]byte("GET")),
		base64.StdEncoding.EncodeToString([]byte("outbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	ctx, span := tracer.Start(context.Background(), "testspan")
	resp := f.OnRequestHeaders(ctx, &req)
	var ok bool
	ir, ok := resp.Response.(*ext_proc.ProcessingResponse_ImmediateResponse)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", ir, resp)
	}

	expectedCode := typev3.StatusCode_Forbidden
	if ir.ImmediateResponse.Status.Code != expectedCode {
		t.Fatalf("Expected status code %v but got %v", expectedCode, ir.ImmediateResponse.Status.Code)
	}

	// Validate expected span data.
	span.End()
	spanStubs := exp.GetSpans()
	require.Len(t, spanStubs, 1)
	spanStub := spanStubs[0]
	var foundException bool
	var foundExceptionType bool
	var foundExceptionMessage bool
	for _, event := range spanStub.Events {
		if event.Name == "exception" {
			foundException = true
			for _, eventAttribute := range event.Attributes {
				if eventAttribute.Key == "exception.type" &&
					eventAttribute.Value.AsString() == "*errors.errorString" {
					foundExceptionType = true
				}

				if eventAttribute.Key == "exception.message" &&
					eventAttribute.Value.AsString() == "RequestNotAllowed" {
					foundExceptionMessage = true
				}

				if foundExceptionType && foundExceptionMessage {
					break
				}
			}

			require.True(t,
				foundExceptionType,
				"did not find expected exception.type attribute: %v",
				event.Attributes)
			require.True(t,
				foundExceptionMessage,
				"did not find expected exception.message attribute: %v",
				event.Attributes)
			break
		}
	}

	require.True(t, foundException, "did not find expected exception event: %v", spanStub.Events)
}

func Test_RequestHeader_MethodDisallowed(t *testing.T) {
	tracer := trace.NewNoopTracerProvider().Tracer("test")
	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action1")),
		base64.StdEncoding.EncodeToString([]byte("POST")),
		base64.StdEncoding.EncodeToString([]byte("inbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	resp := f.OnRequestHeaders(context.Background(), &req)
	var ok bool
	ir, ok := resp.Response.(*ext_proc.ProcessingResponse_ImmediateResponse)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", ir, resp)
	}

	expectedCode := typev3.StatusCode_Forbidden
	if ir.ImmediateResponse.Status.Code != expectedCode {
		t.Fatalf("Expected status code %v but got %v", expectedCode, ir.ImmediateResponse.Status.Code)
	}
}

func Test_RequestBody_ResponseMutationSuccess(t *testing.T) {
	tracer := trace.NewNoopTracerProvider().Tracer("test")
	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	// Send a RequestHeader message first as that is a pre-req for the RequestBody message.
	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action1")),
		base64.StdEncoding.EncodeToString([]byte("GET")),
		base64.StdEncoding.EncodeToString([]byte("inbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	resp := f.OnRequestHeaders(context.Background(), &req)
	var ok bool
	hr, ok := resp.Response.(*ext_proc.ProcessingResponse_RequestHeaders)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	if hr.RequestHeaders.Response.Status != ext_proc.CommonResponse_CONTINUE {
		t.Fatal("Expected request to be allowed but got:", resp)
	}

	// Now send the RequestBody message.
	if err := protojson.Unmarshal([]byte(exampleMutationRequestBody), &req); err != nil {
		panic(err)
	}

	resp = f.OnRequestBody(context.Background(), &req)
	br, ok := resp.Response.(*ext_proc.ProcessingResponse_RequestBody)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	if br.RequestBody.Response.Status != ext_proc.CommonResponse_CONTINUE {
		t.Fatal("Expected request to be allowed but got:", resp)
	}

	if br.RequestBody.Response.BodyMutation.Mutation == nil {
		t.Fatal("Expected mutation response but got nil mutation in response:", resp)
	}

	bm, ok := br.RequestBody.Response.BodyMutation.Mutation.(*ext_proc.BodyMutation_Body)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	expectedBody := "output body"
	if !bytes.Equal(bm.Body, []byte(expectedBody)) {
		t.Fatalf("Expected response body to be %q but got %q", expectedBody, string(bm.Body))
	}

	if len(br.RequestBody.Response.HeaderMutation.SetHeaders) != 1 {
		t.Fatalf("Expected one header to get added but got: %v", br.RequestBody.Response.HeaderMutation.SetHeaders)
	}

	hv := br.RequestBody.Response.HeaderMutation.SetHeaders[0]
	expectedContentLengthKey := "Content-Length"
	if hv.Header.Key != expectedContentLengthKey {
		t.Fatalf("Expected header key %v but got: %v", expectedContentLengthKey, hv.Header.Key)
	}

	expectedContentLength := strconv.Itoa(len(expectedBody))
	if hv.Header.Value != expectedContentLength {
		t.Fatalf("Expected content length header value %v but got: %v", expectedContentLength, hv.Header.Value)
	}
}

func Test_ResponseBody_ResponseMutationSuccess(t *testing.T) {
	tracer := trace.NewNoopTracerProvider().Tracer("test")
	f, err := testOpaFilter(tracer)
	if err != nil {
		t.Fatal(err)
	}

	// Send a RequestHeader message first as that is a pre-req for the ResponseBody message.
	var req ext_proc.ProcessingRequest
	requestHeaders := fmt.Sprintf(exampleRequestHeaders,
		base64.StdEncoding.EncodeToString([]byte("/api/action1")),
		base64.StdEncoding.EncodeToString([]byte("GET")),
		base64.StdEncoding.EncodeToString([]byte("inbound")))
	if err := protojson.Unmarshal([]byte(requestHeaders), &req); err != nil {
		panic(err)
	}

	resp := f.OnRequestHeaders(context.Background(), &req)
	var ok bool
	hr, ok := resp.Response.(*ext_proc.ProcessingResponse_RequestHeaders)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	if hr.RequestHeaders.Response.Status != ext_proc.CommonResponse_CONTINUE {
		t.Fatal("Expected request to be allowed but got:", resp)
	}

	// Now send the ResponseBody message.
	if err := protojson.Unmarshal([]byte(exampleMutationResponseBody), &req); err != nil {
		panic(err)
	}

	resp = f.OnResponseBody(context.Background(), &req)
	br, ok := resp.Response.(*ext_proc.ProcessingResponse_ResponseBody)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	if br.ResponseBody.Response.Status != ext_proc.CommonResponse_CONTINUE {
		t.Fatal("Expected request to be allowed but got:", resp)
	}

	if br.ResponseBody.Response.BodyMutation.Mutation == nil {
		t.Fatal("Expected mutation response but got nil mutation in response:", resp)
	}

	bm, ok := br.ResponseBody.Response.BodyMutation.Mutation.(*ext_proc.BodyMutation_Body)
	if !ok {
		t.Fatalf("Expected response type to be %T but got: %v", hr, resp)
	}

	expectedBody := "output body"
	if !bytes.Equal(bm.Body, []byte(expectedBody)) {
		t.Fatalf("Expected response body to be %q but got %q", expectedBody, string(bm.Body))
	}

	if len(br.ResponseBody.Response.HeaderMutation.SetHeaders) != 1 {
		t.Fatalf("Expected one header to get added but got: %v", br.ResponseBody.Response.HeaderMutation.SetHeaders)
	}

	hv := br.ResponseBody.Response.HeaderMutation.SetHeaders[0]
	expectedContentLengthKey := "Content-Length"
	if hv.Header.Key != expectedContentLengthKey {
		t.Fatalf("Expected header key %v but got: %v", expectedContentLengthKey, hv.Header.Key)
	}

	expectedContentLength := strconv.Itoa(len(expectedBody))
	if hv.Header.Value != expectedContentLength {
		t.Fatalf("Expected content length header value %v but got: %v", expectedContentLength, hv.Header.Value)
	}
}

func Test_OpaPolicyBundleDownload_Failure(t *testing.T) {
	exp := tracetest.NewInMemoryExporter()
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithSyncer(exp),
	)
	tracer := tp.Tracer("tracer")
	ctx, testSpan := tracer.Start(context.Background(), "testspan")
	_, err := NewHttpFilterFactory(ctx, tracer, configuration.PolicyEngine{
		BundleResource: "non-existent-server.com:1234/policy-bundle:latest",
	})

	require.NotNilf(t, err, "expected NewHttpFilterFactory to fail")

	// Validate expected span data.
	testSpan.End()
	spanStubs := exp.GetSpans()
	require.Len(t, spanStubs, 3)
	downloadBundleSpan := extractSpan(spanStubs, "downloadOpaPolicyBundle")
	require.NotNilf(t,
		downloadBundleSpan,
		"did not find expected span with name: %s in spanstubs: %v",
		"downloadOpaPolicyBundle",
		spanStubs)
	var foundException bool
	var foundExceptionType bool
	var foundExceptionMessage bool
	expectedExceptionMessage := "failed to pull non-existent-server.com:1234/policy-bundle:latest: " +
		"download for 'non-existent-server.com:1234/policy-bundle:latest' " +
		"failed: failed to resolve non-existent-server.com:1234/policy-bundle:latest: " +
		"failed to do request: Head \"https://non-existent-server.com:1234/v2/" +
		"policy-bundle/manifests/latest\":"
	for _, event := range downloadBundleSpan.Events {
		if event.Name == "exception" {
			foundException = true
			for _, eventAttribute := range event.Attributes {
				if eventAttribute.Key == "exception.type" &&
					eventAttribute.Value.AsString() == "*fmt.wrapError" {
					foundExceptionType = true
				}

				if eventAttribute.Key == "exception.message" &&
					strings.Contains(eventAttribute.Value.AsString(), expectedExceptionMessage) {
					foundExceptionMessage = true
				}

				if foundExceptionType && foundExceptionMessage {
					break
				}
			}

			require.True(t,
				foundExceptionType,
				"did not find expected exception.type attribute: %v",
				event.Attributes)
			require.True(t,
				foundExceptionMessage,
				"did not find expected exception.message attribute: %v",
				event.Attributes)
			break
		}
	}

	require.True(t, foundException, "did not find expected exception event: %v", downloadBundleSpan.Events)
	require.Truef(
		t,
		strings.Contains(downloadBundleSpan.Status.Description, expectedExceptionMessage),
		"Did not find expected description in downloadBundleSpan.Status.Description")
	require.Equalf(
		t,
		testSpan.SpanContext().SpanID(),
		downloadBundleSpan.Parent.SpanID(),
		"downloadBundleSpan should be a child of the test span")
}

func testOpaFilter(tracer trace.Tracer) (filter.HttpFilter, error) {
	module := `
		package ccr.policy

		import future.keywords

		default on_request_headers = {
			"allowed": false,
			"http_status": 403,
			"body": "RequestNotAllowed"
		}

		on_request_headers := response {
			is_inbound_request == true
			some h1 in input.requestHeaders.headers.headers
			h1.key == ":path"
			h1.rawValue == base64.encode("/api/action1")

			some h2 in input.requestHeaders.headers.headers
			h2.key == ":method"
			h2.rawValue == base64.encode("GET")
			response := {
				"allowed": true,
				"context": {
					"path": "/api/action1"
				}
			}
		}

		is_inbound_request := true if {
			some header in input.requestHeaders.headers.headers
			header.key == "x-ccr-request-direction"
			base64.decode(header.rawValue) == "inbound"
		} else := false

		default on_request_body = false

		on_request_body := response {
			input.context.path == "/api/action1"
			input.requestBody.body == "aW5wdXQgYm9keQ=="
			response := {
				"allowed": true,
				"body": "output body"
			}
		}

		default on_response_headers = true

		default on_response_body = true

		on_response_body := response {
			input.context.path == "/api/action1"
			input.responseBody.body == "aW5wdXQgYm9keQ=="
			response := {
				"allowed": true,
				"body": "output body"
			}
		}
		`

	ff, err := NewHttpFilterFactory(context.Background(), tracer, configuration.PolicyEngine{
		Modules: map[string]string{
			"example.rego": module,
		},
	})
	if err != nil {
		return nil, err
	}

	return ff.CreateFilter(), nil
}

func extractSpan(ss tracetest.SpanStubs, name string) *tracetest.SpanStub {
	for _, s := range ss {
		if s.Name == name {
			return &s
		}
	}

	return nil
}
