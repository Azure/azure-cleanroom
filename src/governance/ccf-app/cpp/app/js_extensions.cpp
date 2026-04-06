// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

// Reference code:
// https://github.com/microsoft/CCF/blob/main/src/apps/js_generic/js_generic.cpp
// https://github.com/microsoft/CCF/blob/main/samples/apps/programmability/programmability.cpp

#include "app/js_extensions.h"

#include "app/checks.h"
#include "ccf/app_interface.h"
#include "ccf/ds/logger.h"
#include "ccf/js/samples/governance_driven_registry.h"
#include "crypto/certs.h"

#include <cstdlib>
#include <httplib.h>
#include <nlohmann/json.hpp>
#include <openssl/crypto.h>
#include <stdexcept>

namespace cleanroomapp::js::extensions
{
  // Note (gsinha): This method is based off ccf's extract_string_array but adds
  // support for "bool allow_empty".
  static JSValue extract_string_array(
    ccf::js::core::Context& jsctx,
    JSValueConst& argv,
    std::vector<std::string>& out,
    bool allow_empty)
  {
    auto args = jsctx.wrap(argv);

    if (!JS_IsArray(jsctx, argv))
    {
      return JS_ThrowTypeError(jsctx, "First argument must be an array");
    }

    auto len_val = args["length"];
    uint32_t len = 0;
    if (JS_ToUint32(jsctx, &len, len_val.val))
    {
      return ccf::js::core::constants::Exception;
    }

    if (len == 0)
    {
      if (allow_empty)
      {
        return ccf::js::core::constants::Undefined;
      }

      return JS_ThrowRangeError(
        jsctx, "First argument must be a non-empty array");
    }

    for (uint32_t i = 0; i < len; i++)
    {
      auto arg_val = args[i];
      if (!arg_val.is_str())
      {
        return JS_ThrowTypeError(
          jsctx,
          "First argument must be an array of strings, found non-string");
      }
      auto s = jsctx.to_str(arg_val);
      if (!s)
      {
        return JS_ThrowTypeError(
          jsctx, "Failed to extract C string from JS string at position %d", i);
      }
      out.push_back(*s);
    }

    return ccf::js::core::constants::Undefined;
  }

  // Makes an HTTP POST request to the given host:port/path with the
  // provided JSON body. Returns the response body as a string.
  static std::string http_post(
    const std::string& host,
    int port,
    const std::string& path,
    const std::string& body)
  {
    httplib::Client client(host, port);
    client.set_connection_timeout(30);
    client.set_read_timeout(30);
    client.set_write_timeout(30);

    auto res = client.Post(path, body, "application/json");
    if (!res)
    {
      throw std::runtime_error(
        "HTTP POST to " + host + ":" + std::to_string(port) + path +
        " failed: " + httplib::to_string(res.error()));
    }

    if (res->status != 200)
    {
      throw std::runtime_error(
        "HTTP POST returned status " + std::to_string(res->status) + ": " +
        res->body);
    }

    return res->body;
  }

  JSValue js_generate_self_signed_cert(
    JSContext* ctx, JSValueConst, int argc, JSValueConst* argv)
  {
    if (argc != 5 && argc != 6)
      return JS_ThrowTypeError(
        ctx, "Passed %d arguments, but expected 5 or 6", argc);

    ccf::js::core::Context& jsctx =
      *(ccf::js::core::Context*)JS_GetContextOpaque(ctx);

    auto priv_key = jsctx.to_str(argv[0]);
    if (!priv_key)
    {
      return ccf::js::core::constants::Exception;
    }

    auto subject_name = jsctx.to_str(argv[1]);
    if (!subject_name)
    {
      return ccf::js::core::constants::Exception;
    }

    std::vector<std::string> subject_alt_names;
    bool allow_empty = true;
    JSValue rv =
      extract_string_array(jsctx, argv[2], subject_alt_names, allow_empty);
    if (!JS_IsUndefined(rv))
    {
      return JS_ThrowTypeError(ctx, "3rd argument must be a string array");
    }

    auto sans = ccf::crypto::sans_from_string_list(subject_alt_names);

    int32_t validity_period_days;
    if (JS_ToInt32(ctx, &validity_period_days, argv[3]) < 0)
    {
      return ccf::js::core::constants::Exception;
    }

    const auto v = argv[4];
    if (!JS_IsBool(v))
    {
      return JS_ThrowTypeError(ctx, "5th argument must be a boolean");
    }
    auto ca = JS_ToBool(ctx, v);

    std::optional<int> ca_path_len_constraint;
    if (argc == 6)
    {
      int32_t value;
      if (JS_ToInt32(ctx, &value, argv[5]) < 0)
      {
        return ccf::js::core::constants::Exception;
      }
      ca_path_len_constraint = value;
    }

    try
    {
      auto kp = cleanroom::crypto::make_key_pair(priv_key.value());
      OPENSSL_cleanse(priv_key.value().data(), priv_key.value().size());
      using namespace std::literals;
      // valid_from starts a day before the current time so validity period is
      // adjusted accordingly by increasing the input validity_period_days by 1.
      auto valid_from =
        ccf::ds::to_x509_time_string(std::chrono::system_clock::now() - 24h);
      ccf::crypto::Pem cert_pem = cleanroom::crypto::create_self_signed_cert(
        kp,
        subject_name.value(),
        sans,
        valid_from,
        validity_period_days + 1,
        ca,
        ca_path_len_constraint);

      auto r = jsctx.new_obj();
      JS_CHECK_EXC(r);
      auto cert = jsctx.new_string_len((char*)cert_pem.data(), cert_pem.size());
      JS_CHECK_EXC(cert);
      JS_CHECK_SET(r.set("cert", std::move(cert)));

      return r.take();
    }
    catch (const std::exception& exc)
    {
      return JS_ThrowInternalError(
        ctx, "Failed to generate self signed cert: %s", exc.what());
    }
  }

  JSValue js_verify_cvm_snp_attestation(
    JSContext* ctx, JSValueConst, int argc, JSValueConst* argv)
  {
    if (argc != 1)
      return JS_ThrowTypeError(
        ctx, "Passed %d arguments, but expected 1", argc);

    ccf::js::core::Context& jsctx =
      *(ccf::js::core::Context*)JS_GetContextOpaque(ctx);

    auto evidence_str = jsctx.to_str(argv[0]);
    if (!evidence_str)
    {
      return ccf::js::core::constants::Exception;
    }

    try
    {
      // Parse input to validate it is valid JSON.
      auto input = nlohmann::json::parse(evidence_str.value());

      // Read host and port from environment variables, defaulting to
      // localhost:8901 if not set.
      const char* host_env = std::getenv("CCR_ATTESTATION_HOST");
      const char* port_env = std::getenv("CCR_ATTESTATION_PORT");
      std::string host = host_env ? host_env : "localhost";
      int port = port_env ? std::stoi(port_env) : 8901;

      CCF_APP_INFO(
        "Verifying CVM SNP attestation via {}:{}/snp/verify", host, port);

      // Make an HTTP POST call to the local attestation verification
      // service with the evidence JSON as the request body.
      auto response_str = http_post(host, port, "/snp/verify", input.dump());

      // Return as parsed JSON object.
      return JS_ParseJSON(
        ctx, response_str.c_str(), response_str.size(), "<verify>");
    }
    catch (const nlohmann::json::parse_error& exc)
    {
      return JS_ThrowInternalError(
        ctx, "Failed to parse evidence input: %s", exc.what());
    }
    catch (const std::exception& exc)
    {
      return JS_ThrowInternalError(
        ctx, "Failed to verify CVM SNP attestation: %s", exc.what());
    }
  }

  JSValue js_generate_endorsed_cert(
    JSContext* ctx, JSValueConst, int argc, JSValueConst* argv)
  {
    if (argc != 7 && argc != 8)
      return JS_ThrowTypeError(
        ctx, "Passed %d arguments, but expected 7 or 8", argc);

    ccf::js::core::Context& jsctx =
      *(ccf::js::core::Context*)JS_GetContextOpaque(ctx);

    auto public_key = jsctx.to_str(argv[0]);
    if (!public_key)
    {
      return ccf::js::core::constants::Exception;
    }

    auto subject_name = jsctx.to_str(argv[1]);
    if (!subject_name)
    {
      return ccf::js::core::constants::Exception;
    }

    std::vector<std::string> subject_alt_names;
    bool allow_empty = true;
    JSValue rv =
      extract_string_array(jsctx, argv[2], subject_alt_names, allow_empty);
    if (!JS_IsUndefined(rv))
    {
      return JS_ThrowTypeError(ctx, "3rd argument must be a string array");
    }

    auto sans = ccf::crypto::sans_from_string_list(subject_alt_names);

    int32_t validity_period_days;
    if (JS_ToInt32(ctx, &validity_period_days, argv[3]) < 0)
    {
      return ccf::js::core::constants::Exception;
    }

    auto issuer_private_key = jsctx.to_str(argv[4]);
    if (!issuer_private_key)
    {
      return ccf::js::core::constants::Exception;
    }

    auto issuer_cert = jsctx.to_str(argv[5]);
    if (!issuer_cert)
    {
      return ccf::js::core::constants::Exception;
    }

    const auto v = argv[6];
    if (!JS_IsBool(v))
    {
      return JS_ThrowTypeError(ctx, "7th argument must be a boolean");
    }
    auto ca = JS_ToBool(ctx, v);

    std::optional<int> ca_path_len_constraint;
    if (argc == 8)
    {
      int32_t value;
      if (JS_ToInt32(ctx, &value, argv[7]) < 0)
      {
        return ccf::js::core::constants::Exception;
      }
      ca_path_len_constraint = value;
    }
    try
    {
      using namespace std::literals;
      // valid_from starts a day before the current time so validity period is
      // adjusted accordingly by increasing the input validity_period_days by 1.
      auto valid_from =
        ccf::ds::to_x509_time_string(std::chrono::system_clock::now() - 24h);
      auto valid_to = cleanroom::crypto::compute_cert_valid_to_string(
        valid_from, validity_period_days + 1);

      ccf::crypto::Pem cert_pem = cleanroom::crypto::create_endorsed_cert(
        public_key.value(),
        subject_name.value(),
        sans,
        valid_from,
        valid_to,
        issuer_private_key.value(),
        issuer_cert.value(),
        ca,
        ca_path_len_constraint);

      auto r = jsctx.new_obj();
      JS_CHECK_EXC(r);
      auto cert = jsctx.new_string_len((char*)cert_pem.data(), cert_pem.size());
      JS_CHECK_EXC(cert);
      JS_CHECK_SET(r.set("cert", std::move(cert)));

      return r.take();
    }
    catch (const std::exception& exc)
    {
      return JS_ThrowInternalError(
        ctx, "Failed to generate endorsed cert: %s", exc.what());
    }
  }
}