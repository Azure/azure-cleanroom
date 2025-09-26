class Constants:
    """Constants used in the Cleanroom Spark application."""

    ALLOW_ALL_POLICY_BASE64 = (
        "cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA"
        + "6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6I"
        + "HRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9"
        + "saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV"
        + "2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGx"
        + "vd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSw"
        + "gImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h"
        + "1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWl"
        + "uZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImF"
        + "sbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cmd"
        + "ldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHs"
        + "iYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQ"
        + "gOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg=="
    )

    ALLOW_ALL_POLICY_HASH = (
        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
    )

    # The Label to decorate the pod so that the mutator webhook will pick it up
    # and inject the CCE policy.
    CCE_POLICY_INJECTOR_LABEL = "inject-cce-policy"

    # The annotation key that specifies the node in which the CCE policy needs to be injected.
    CCE_POLICY_ANNOTATION_NAME_LABEL = "cce-policy-annotation-name"

    # The annotation key that specifies the config map which contains the CCE policy to be
    # injected into the pod.
    CCE_POLICY_CONFIG_MAP_ANNOTATION = "microsoft.cleanroom.spark/cce-policy-map"

    # The key in the config map that contains the CCE policy in base64 format.
    CCE_POLICY_CONFIG_MAP_POLICY_KEY = "policy_base64"

    # The name of the webhook service which injects the CCE Policy into the Spark Pods.
    CCE_POLICY_INJECTOR_WEBHOOK_NAME = "cce-policy-injector"

    # The service name to use for any OpenTelemetry instrumentation.
    OTEL_SERVICE_NAME = "cleanroom-spark-frontend"

    # The environment variable key for OpenTelemetry trace context.
    OTEL_TRACE_CONTEXT_ENV_KEY = "OTEL_TRACE_CONTEXT_BASE64"
