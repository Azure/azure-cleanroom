// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the Apache 2.0 License.
/**
 * This module describes the global {@linkcode cleanroom} variable.
 * Direct access of this module or the {@linkcode cleanroom} variable is
 * typically not needed as all of its functionality is exposed
 * via other, often more high-level, modules.
 *
 * Accessing the {@linkcode cleanroom} global in a type-safe way is done
 * as follows:
 *
 * ```
 * import { cleanroom } from '/global.cleanroom.js';
 * ```
 *
 * @module
 */
// The global cleanroom variable and associated types are exported
// as a regular module instead of using an ambient namespace
// in a .d.ts definition file.
// This avoids polluting the global namespace.
export const cleanroom = globalThis.cleanroom;
