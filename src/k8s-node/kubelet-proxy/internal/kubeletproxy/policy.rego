package kubeletproxy.policy

import rego.v1

default allowed := false

# Exact match: URI equals an allowed API path.
allowed if {
    some api in data.allowed_apis
    api == input.uri
}

# Subpath match: URI starts with an allowed API path followed by "/".
allowed if {
    some api in data.allowed_apis
    startswith(input.uri, concat("", [api, "/"]))
}

# Query parameter match: URI starts with an allowed API path followed by "?".
allowed if {
    some api in data.allowed_apis
    startswith(input.uri, concat("", [api, "?"]))
}
