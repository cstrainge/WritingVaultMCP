# v4 Phase 6 hostile managed-image review

Review date: 2026-09-28  
Scope: transport bounds, byte/type validation, decoder safety, orientation/color conversion, metadata removal, renditions, immutable masters, primary-image races, search paging, deletion, capacity, and verified backup  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Lie about media type or send malformed/truncated data | Detect from bytes and reject before writing | Image tests cover media mismatch and malformed content; decoder accepts only complete successful PNG/JPEG/WebP decodes. |
| Send a decompression bomb or excessive payload | Stop at encoded-byte, decoded-pixel, rendition-edge, and rendition-byte ceilings | 20 MiB input, 50-million-pixel decode, 2048/5 MiB display, and 512/512 KiB thumbnail limits are enforced. |
| Include orientation, color profiles, metadata, or transparency | Render oriented sRGB JPEGs with metadata removed and report flattening | Decode/re-render path uses sRGB bitmaps, applies encoded origin, flattens onto the requested color, and returns a warning. |
| Attach duplicate bytes or force a hash collision | Reject same-owner duplicates and compare existing bytes in constant time before reuse | Content-addressed master checks and duplicate-row checks run within coordinated mutation handling. |
| Race two primary selections | Serialize and leave at most one active primary | Primary clear/promote/insert occurs in the write coordinator transaction; lifecycle deletion deterministically promotes the lowest active successor. |
| Search a large image set | Filter and page in Access without returning bytes or issuing one owner lookup per row | `image_search` applies all filters and `TOP limit+1` in SQL and joins owner labels in the same query. |
| Delete an owner or cross continuity | Hide its images and reject explicit viewing outside selected scope | Search joins active selected-continuity owners; view/list resolve owner and image scope semantically. |
| Fill Access near its file ceiling | Warn at 1.2 GiB and reject projected writes above 1.5 GiB | Health exposes the capacity band; attach checks projected rendition growth before insertion. |
| Back up an image-backed vault | Verify every immutable master under the same asset-root contract | The end-to-end MCP workflow creates an image and then completes a verified database-plus-assets backup. |

## Findings resolved during review

1. Attach metadata fields were not all length-checked. Title, caption, alt text, role, and canon status now enforce public/storage bounds on attach and update.
2. A source resolved before queueing could be deleted before commit. The transaction now revalidates the source as active.
3. Partial decoder results were accepted. Only `SKCodecResult.Success` is now allowed, with positive dimensions, a single frame, and a 50-million-pixel ceiling.
4. JPEG renditions began at unnecessarily low quality. Encoding now starts at 95 and reduces only as required by the byte ceiling.
5. Image search loaded broad result sets and performed owner lookups per item. Filtering, ordering, paging, owner labels, and selected-continuity checks now occur in one bounded Access query.
6. Master paths were stored as `assets/originals/...` even though backup already treated the field as relative to the assets root. Stored paths are now `originals/...`; ingestion, backup, and restore resolve one safe path.
7. Temporary originals could survive failed writes. Temporary files and newly-created masters now participate in rollback cleanup.
8. Primary deletion/update behavior could leave no primary or an arbitrary successor. The active lowest semantic image order is used deterministically, while a sole image remains primary.

## Verification

- Phase 6 focused Debug gate: **5 passed, 0 failed, 0 skipped**.
- Full Debug regression suite: **148 passed, 0 failed, 0 skipped**.
- The complete Phase 7 workflow verifies image attach, MCP image-block view, and image-backed backup.
- Debug build: zero warnings and zero errors.
- No production asset or database path was used.

## Unresolved findings

None.
