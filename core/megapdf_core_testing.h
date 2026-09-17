// Hooks for the core's own tests (core/tests), not part of the ABI the apps bind (#145).
//
// The page check lets go of the core lock between its stages, and its tests must prove that
// without racing the machine's speed: a hook parks the check at a stage, with the lock let go,
// for as long as the test needs.
#ifndef MEGAPDF_CORE_TESTING_H
#define MEGAPDF_CORE_TESTING_H

#include "megapdf_core.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*megapdf_page_check_stage_hook)(void* context);

/**
 * While set, megapdf_page_regeneration_verdict_cancellable() lets go of the core lock at every
 * stage, however short, and calls `hook(context)` on its own thread before taking the lock
 * back; the hook may call into the core. NULL restores normal hand-overs. Set it only while no
 * page check runs.
 */
MEGAPDF_API void megapdf_testing_set_page_check_hook(megapdf_page_check_stage_hook hook, void* context);

/**
 * The layout guard's compare (#118) of two pages, as it judges a reopened page before and after
 * a rewrite: the render and every text object, by megapdf_layout_verdict's budgets, with nothing
 * marked as edited. 1 or 0 as the verdict's `editable`, MEGAPDF_ERR_ARGUMENT for a NULL argument.
 * Like the guard, it may swap the pages' large images for stand-ins (#151): the documents are
 * spent afterwards.
 */
MEGAPDF_API int megapdf_testing_compare_pages(const megapdf_page* was, const megapdf_page* now, megapdf_layout_verdict* out);

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif /* MEGAPDF_CORE_TESTING_H */
