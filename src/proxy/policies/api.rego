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

# These are the infrastructure APIs that are defined by the cleanroom.
# The definition of these can be found in code_launcher.py.
post_regex = "^\/gov\/(exportLogs$|exportTelemetry$|.+\/start$)"
get_regex = "^\/gov\/.+\/status$"

on_request_headers = result if {
    extract_method == "POST"
    regex.match(post_regex, extract_path)
    result := {"allowed": true} 
} else := result if {
    extract_method == "GET"
    regex.match(get_regex, extract_path)
    result := {"allowed": true} 
}

default on_response_body = true
default on_response_headers = true

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
