// Copyright 2014 The PDFium Authors
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Original code copyright 2014 Foxit Software Inc. http://www.foxitsoftware.com

#ifndef PUBLIC_FPDF_SAVE_H_
#define PUBLIC_FPDF_SAVE_H_

// clang-format off
// NOLINTNEXTLINE(build/include)
#include "fpdfview.h"

#ifdef __cplusplus
extern "C" {
#endif

// Structure for custom file write
typedef struct FPDF_FILEWRITE_ {
  //
  // Version number of the interface. Currently must be 1.
  //
  int version;

  // Method: WriteBlock
  //          Output a block of data in your custom way.
  // Interface Version:
  //          1
  // Implementation Required:
  //          Yes
  // Comments:
  //          Called by function FPDF_SaveDocument
  // Parameters:
  //          self        -   Pointer to the structure itself
  //          data        -   Pointer to a buffer to output
  //          size        -   The size of the buffer.
  // Return value:
  //          Should be non-zero if successful, zero for error.
  int (*WriteBlock)(struct FPDF_FILEWRITE_* self,
                    const void* data,
                    unsigned long size);
} FPDF_FILEWRITE;

// Flags for FPDF_SaveAsCopy().
// FPDF_INCREMENTAL and FPDF_NO_INCREMENTAL cannot be used together.
#define FPDF_INCREMENTAL (1 << 0)
#define FPDF_NO_INCREMENTAL (1 << 1)
 // Deprecated. Use FPDF_REMOVE_SECURITY instead.
 // TODO(crbug.com/42270430): Remove FPDF_REMOVE_SECURITY_DEPRECATED.
#define FPDF_REMOVE_SECURITY_DEPRECATED 3
#define FPDF_REMOVE_SECURITY (1 << 2)
// Experimental. Subsets any embedded font files for new text objects added to
// the document.
#define FPDF_SUBSET_NEW_FONTS (1 << 3)

// Function: FPDF_SaveAsCopy
//          Saves the copy of specified document in custom way.
// Parameters:
//          document        -   Handle to document, as returned by
//                              FPDF_LoadDocument() or FPDF_CreateNewDocument().
//          file_write      -   A pointer to a custom file write structure.
//          flags           -   Flags above that affect how the PDF gets saved.
//                              Pass in 0 when there are no flags.
// Return value:
//          TRUE for succeed, FALSE for failed.
//
FPDF_EXPORT FPDF_BOOL FPDF_CALLCONV FPDF_SaveAsCopy(FPDF_DOCUMENT document,
                                                    FPDF_FILEWRITE* file_write,
                                                    FPDF_DWORD flags);

// Function: FPDF_SaveWithVersion
//          Same as FPDF_SaveAsCopy(), except the file version of the
//          saved document can be specified by the caller.
// Parameters:
//          document        -   Handle to document.
//          file_write      -   A pointer to a custom file write structure.
//          flags           -   The creating flags.
//          file_version    -   The PDF file version. File version: 14 for 1.4,
//                              15 for 1.5, ...
// Return value:
//          TRUE if succeed, FALSE if failed.
//
FPDF_EXPORT FPDF_BOOL FPDF_CALLCONV
FPDF_SaveWithVersion(FPDF_DOCUMENT document,
                     FPDF_FILEWRITE* file_write,
                     FPDF_DWORD flags,
                     int file_version);

// Experimental API (MegaPDF patch series).
// Function: FPDF_SaveAsCopyWithSecurity
//          Saves a copy of the document encrypted with the standard security
//          handler at revision 6 (AES-256, ISO 32000-2), replacing whatever
//          security it had. To save a copy with no security, use
//          FPDF_SaveAsCopy() with FPDF_REMOVE_SECURITY.
// Parameters:
//          document        -   Handle to document.
//          file_write      -   A pointer to a custom file write structure.
//          flags           -   Flags as for FPDF_SaveAsCopy(). The copy is
//                              always written in full, so FPDF_INCREMENTAL
//                              makes the call fail.
//          user_password   -   UTF-8, NUL-terminated: opens the document with
//                              the permissions below. May be empty, so the
//                              document opens without one.
//          owner_password  -   UTF-8, NUL-terminated: opens it with every
//                              permission. Empty or NULL means the same as
//                              `user_password`.
//          permissions     -   The P bits granted to a user-password open
//                              (ISO 32000-2, Table 22): 1 << 2 print,
//                              1 << 3 modify, 1 << 4 copy, 1 << 5 annotate,
//                              1 << 8 fill forms, 1 << 9 extract for
//                              accessibility, 1 << 10 assemble,
//                              1 << 11 print at full quality. The reserved
//                              bits are set for the caller.
// Return value:
//          TRUE for success, FALSE for failure.
//
FPDF_EXPORT FPDF_BOOL FPDF_CALLCONV
FPDF_SaveAsCopyWithSecurity(FPDF_DOCUMENT document,
                            FPDF_FILEWRITE* file_write,
                            FPDF_DWORD flags,
                            FPDF_BYTESTRING user_password,
                            FPDF_BYTESTRING owner_password,
                            unsigned long permissions);

#ifdef __cplusplus
}
#endif

#endif  // PUBLIC_FPDF_SAVE_H_
