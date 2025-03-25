// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

// Reference code:
// https://github.com/microsoft/CCF/blob/main/src/apps/js_generic/js_generic.cpp
// https://github.com/microsoft/CCF/blob/main/samples/apps/programmability/programmability.cpp

#include "app/checks.h"
#include "app/js_extensions.h"
#include "ccf/app_interface.h"
#include "ccf/js/samples/governance_driven_registry.h"
#include "crypto/certs.h"

#include <openssl/crypto.h>

namespace cleanroomapp
{
  class CleanRoomExtension : public ccf::js::extensions::ExtensionInterface
  {
  public:
    // Store any objects/state which the extension's functions might need on
    // this extension object.
    // In this case, since the extension adds a function that wants to read from
    // the KV, it needs the current request's Tx.
    ccf::kv::ReadOnlyTx* tx;

    CleanRoomExtension(ccf::kv::ReadOnlyTx* t) : tx(t) {}

    void install(ccf::js::core::Context& ctx) override;
  };

  void CleanRoomExtension::install(ccf::js::core::Context& ctx)
  {
    // Nest all of the extension's crypto functions in a single object, rather
    // than inserting directly into the global namespace.
    auto crypto_object = ctx.new_obj();

    // Insert functions into the JS environment, called at
    // my_object.<function_name>
    crypto_object.set(
      // Name of field on object
      "generateSelfSignedCert",
      ctx.new_c_function(
        // C/C++ function implementing this JS function
        cleanroomapp::js::extensions::js_generate_self_signed_cert,
        // Repeated name of function, used in callstacks
        "generateSelfSignedCert",
        // Number of arguments to this function
        6));

    crypto_object.set(
      // Name of field on object
      "generateEndorsedCert",
      ctx.new_c_function(
        // C/C++ function implementing this JS function
        cleanroomapp::js::extensions::js_generate_endorsed_cert,
        // Repeated name of function, used in callstacks
        "generateEndorsedCert",
        // Number of arguments to this function
        8));

    auto cleanroom_object =
      ctx.get_or_create_global_property("cleanroom", ctx.new_obj());
    cleanroom_object.set("crypto", std::move(crypto_object));
  }

  class CleanRoomHandlers : public ccf::js::GovernanceDrivenJSRegistry
  {
  public:
    CleanRoomHandlers(ccf::AbstractNodeContext& context) :
      ccf::js::GovernanceDrivenJSRegistry(context)
    {}

    ccf::js::extensions::Extensions get_extensions(
      const ccf::endpoints::EndpointContext& endpoint_ctx) override
    {
      ccf::js::extensions::Extensions extensions;

      extensions.push_back(
        std::make_shared<CleanRoomExtension>(&endpoint_ctx.tx));

      return extensions;
    }
  };
}

namespace ccf
{
  std::unique_ptr<ccf::endpoints::EndpointRegistry> make_user_endpoints(
    ccf::AbstractNodeContext& context)
  {
    return std::make_unique<cleanroomapp::CleanRoomHandlers>(context);
  }
}