// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
#pragma once

#include <stdexcept>

#define JS_CHECK_EXC(val) \
  do \
  { \
    if (val.is_exception()) \
    { \
      return val.take(); \
    } \
  } while (0)

#define JS_CHECK_SET(val) \
  do \
  { \
    if (val != 1) \
    { \
      return ccf::js::core::constants::Exception; \
    } \
  } while (0)

#define JS_CHECK_OR_THROW(val) \
  do \
  { \
    if (val != 1) \
    { \
      throw std::runtime_error("Unable to populate JS object"); \
    } \
  } while (0)
