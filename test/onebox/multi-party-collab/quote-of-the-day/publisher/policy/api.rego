package ccr.policy

import future.keywords

default on_request_headers = {
    "allowed": false,
    "http_status": 403,
    "body": {
        "code": "RequestNotAllowed",
        "message": "Failed ccr policy check: Requested API is not allowed"
    }
}

default on_request_body = {
    "allowed": false,
    "http_status": 403,
    "body": {
        "code": "RequestBodyNotAllowed",
        "message": "Failed ccr policy check: Requested API body is not allowed"
    }
}

on_request_headers = result if {
    is_outbound_request == true
    extract_url == "api.quotable.io"
    extract_path == "/quotes/random"
    extract_method == "GET"
    result := {"allowed": true}
}

default on_response_body = true
default on_response_headers = true

extract_url := value if {
    filtered := json.filter(input, ["attributes/envoy.filters.http.ext_proc"])
    value := object.get(filtered, ["attributes", "envoy.filters.http.ext_proc", "request.host"], "")
}

is_outbound_request := true if {
    some header in input.requestHeaders.headers.headers
    header.key == "x-ccr-request-direction"
    base64.decode(header.rawValue) == "outbound"
} else := false

extract_path := path if {
    some header in input.requestHeaders.headers.headers
    header.key == ":path"
    path := base64.decode(header.rawValue)
} else := ""

extract_method := method if {
    some header in input.requestHeaders.headers.headers
    header.key == ":method"
    method := base64.decode(header.rawValue)
} else := ""
