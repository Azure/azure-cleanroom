# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT license.

cmake_minimum_required(VERSION 3.16)

project(js_cleanroom LANGUAGES C CXX)

option(
  COMPILE_TARGET
  "Compile target to build for, one of [virtual;sgx;snp], defaults to virtual"
  virtual)

find_package("ccf_${COMPILE_TARGET}" REQUIRED)

add_ccf_app(
  js_cleanroom
  SRCS
    ${CMAKE_CURRENT_SOURCE_DIR}/app/js_generic.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/app/js_extensions.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/crypto/key_pair.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/crypto/openssl/public_key.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/crypto/openssl/key_pair.cpp
  INCLUDE_DIRS
    ${CMAKE_CURRENT_SOURCE_DIR}
  )
