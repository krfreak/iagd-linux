// Case-only alias. Some sources include "StdAfx.h", others "stdafx.h"; MSVC on a
// case-insensitive filesystem never cared. Resolving it here keeps the upstream sources
// byte-identical instead of scattering a rename across the tree.
//
// Named without a directory so it resolves through -I$(SRC_DIR), which points at the
// generated tree rather than at a checked-in copy.
#pragma once
#include "stdafx.h"
