"""Checks that trimming bone tails never moves the skin or changes an export.
Run with:

  blender --background --factory-startup --python addon/smoke/headless_tailtrim.py \
      -- --glb <path.glb> [--tol 0.001]

The GLB is imported and folded as the addon does, twice: one scene is exported
untrimmed, the other is trimmed and exported. The two GLBs must be
byte-identical, and the trimmed scene's deformed skin must match its untrimmed
skin at rest and at sampled clip frames.
"""
import argparse
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import bpy  # noqa: E402

from io_shadowforge import rigfold, scene, tailtrim  # noqa: E402
from smoke.common import enable_gltf_io, fail, script_args  # noqa: E402
from smoke.headless_rigfold import _cases, _max_delta, _snapshot  # noqa: E402


def _import_folded(glb):
    """Fresh scene holding the folded GLB. Each export unfolds and refolds the
    action curves, so every export gets a scene of its own. Returns the
    armature, the skin at the sampled cases, and the cases."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=glb, bone_heuristic='FORTUNE')
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    actions = list(bpy.data.actions)
    rigfold.fold_armature(arm, actions)
    cases = _cases(actions)
    return arm, [_snapshot(arm, a, f) for a, f in cases], cases


def _export_bytes(workdir, name):
    with open(scene.export_scene_glb(workdir, name), "rb") as f:
        return f.read()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--glb", required=True)
    ap.add_argument("--tol", type=float, default=1e-3)
    args = ap.parse_args(script_args())

    enable_gltf_io()
    workdir = tempfile.mkdtemp(prefix="sf_tailtrim_")
    _import_folded(args.glb)
    untrimmed = _export_bytes(workdir, "untrimmed")

    arm, before, cases = _import_folded(args.glb)
    size = tailtrim.model_size(bpy.data.objects)
    trimmed = tailtrim.trim_tails(arm, size)
    print("trimmed %d bones: %s" % (len(trimmed), trimmed))
    if not trimmed:
        fail("TAILTRIM", "nothing was trimmed, so this rig cannot verify the restore")
    same_export = _export_bytes(workdir, "trimmed") == untrimmed

    limit = tailtrim._MAX_FRACTION * size
    longest = max(arm.data.bones, key=lambda b: b.length)
    if longest.length > limit:
        fail("TAILTRIM", "bone '%s' is still %.3f long (limit %.3f)"
             % (longest.name, longest.length, limit))
    if not same_export:
        fail("TAILTRIM", "export after trimming differs from the untrimmed export")

    worst = max(_max_delta(b, _snapshot(arm, a, f))[0]
                for (a, f), b in zip(cases, before))
    print("worst skin delta after trim = %.6f" % worst)
    if worst > args.tol:
        fail("TAILTRIM", "trimming moved the skin by %.6f" % worst)
    print("TAILTRIM-OK")


if __name__ == "__main__":
    main()
