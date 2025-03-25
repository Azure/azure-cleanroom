// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
#pragma once

#include "ccf/js/samples/governance_driven_registry.h"

namespace cleanroomapp::js::extensions
{
  JSValue js_generate_self_signed_cert(
    JSContext* ctx, JSValueConst, int argc, JSValueConst* argv);

  JSValue js_generate_endorsed_cert(
    JSContext* ctx, JSValueConst, int argc, JSValueConst* argv);
}