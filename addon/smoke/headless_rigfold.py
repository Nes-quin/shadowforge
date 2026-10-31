"""Checks that the roll sandwich never moves the skin. Run with:

  blender --background --factory-startup --python addon/smoke/headless_rigfold.py \
      -- --glb <path.glb> [--tol 0.001]

Blender deforms by pose_matrix @ bone.matrix_local^-1 and the fold changes
matrix_local, so bone world poses are the wrong thing to compare. The check is
the deformed skin itself: every sampled clip frame must evaluate to the same
vertex positions before and after folding, and the rest pose must keep an
identity basis.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import bpy  # noqa: E402
from mathutils import Matrix  # noqa: E402

from io_shadowforge import rigfold  # noqa: E402
from smoke.common import enable_gltf_io, script_args  # noqa: E402

FAILURES = []


def _fail(msg):
    print("RIGFOLD-FAIL: " + msg)
    FAILURES.append(msg)


def _eval_verts(dg):
    """Deformed world-space vertex positions of every mesh in the scene."""
    out = {}
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        ev = obj.evaluated_get(dg)
        me = ev.to_mesh()
        mw = ev.matrix_world
        out[obj.name] = [mw @ v.co.copy() for v in me.vertices]
        ev.to_mesh_clear()
    return out


def _max_delta(a, b):
    worst, where = 0.0, None
    for name, pts in a.items():
        other = b.get(name)
        if other is None or len(other) != len(pts):
            _fail("mesh '%s' changed vertex count across the fold" % name)
            continue
        for i, (p, q) in enumerate(zip(pts, other)):
            d = (p - q).length
            if d > worst:
                worst, where = d, "%s[%d]" % (name, i)
    return worst, where


def _snapshot(arm, action, frame):
    """Skin under `action` at `frame`. A None action measures the rest pose.
    The live pose is cleared first so each sample is independent of the last."""
    for pb in arm.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = action
    if action is not None:
        slots = getattr(action, "slots", None)
        if slots:
            arm.animation_data.action_slot = slots[0]
        bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    dg.update()
    verts = _eval_verts(dg)
    arm.animation_data.action = None
    return verts


def _cases(actions, per_clip=4, clips=3):
    """The rest pose plus sample frames spread across the longest clips."""
    out = [(None, 1)]
    longest = sorted(actions, key=lambda a: a.frame_range[0] - a.frame_range[1])[:clips]
    for a in longest:
        lo, hi = a.frame_range
        for k in range(per_clip):
            out.append((a, int(lo + (hi - lo) * k / max(1, per_clip - 1))))
    return out


def _scene_state(arm, actions):
    """Every keyframe and pose channel, for bit-exact comparison."""
    keys = [(fc.data_path, fc.array_index, [tuple(kp.co) for kp in fc.keyframe_points])
            for a in actions for bag in rigfold._channelbags(a) for fc in bag.fcurves]
    pose = [(pb.name, tuple(pb.location), tuple(pb.rotation_quaternion), tuple(pb.scale))
            for pb in arm.pose.bones]
    return keys, pose


def _check(label, cases, before, after, tol):
    worst = 0.0
    for (action, frame), b, a in zip(cases, before, after):
        d, where = _max_delta(b, a)
        worst = max(worst, d)
        if d > tol:
            _fail("%s: %s frame %d moved the skin by %.6f at %s (tol %.6f)"
                  % (label, action.name if action else "<rest pose>", frame, d, where, tol))
    print("  %-28s worst skin delta = %.6f" % (label, worst))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--glb", required=True)
    ap.add_argument("--tol", type=float, default=1e-3)
    args = ap.parse_args(script_args())

    enable_gltf_io()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=args.glb, bone_heuristic='FORTUNE')

    arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    if len(arms) != 1:
        _fail("expected exactly one armature, got %d" % len(arms))
        sys.exit(1)
    arm = arms[0]
    actions = list(bpy.data.actions)
    if not actions:
        _fail("glb has no animation clips to verify against")
        sys.exit(1)

    cases = _cases(actions)
    before = [_snapshot(arm, a, f) for a, f in cases]

    # Sampling leaves the last clip's basis on the pose bones. A fresh import
    # folds from rest, so return to rest before folding.
    _snapshot(arm, None, 1)

    folded, warnings = rigfold.fold_armature(arm, actions)
    print("folded %d bones (%d placement warnings)" % (len(folded), len(warnings)))
    if not folded:
        _fail("nothing was folded, so this rig cannot verify the sandwich")
        sys.exit(1)

    # A folded bone holding a non-identity basis at rest shows as a joint
    # twisted by up to 180 degrees about its own axis.
    for pb in arm.pose.bones:
        if pb.name not in folded:
            continue
        q = pb.matrix_basis.to_quaternion()
        if abs(abs(q.w) - 1.0) > 1e-5:
            _fail("bone '%s' holds a non-identity rest basis after folding: "
                  "(%.4f %.4f %.4f %.4f)" % (pb.name, q.w, q.x, q.y, q.z))

    _check("after fold", cases, before, [_snapshot(arm, a, f) for a, f in cases], args.tol)

    # Each export unfolds and refolds, so the refold must return the scene
    # bit for bit or consecutive exports differ.
    keys, pose = _scene_state(arm, actions)
    with rigfold.unfolded_for_export():
        pass
    keys_after, pose_after = _scene_state(arm, actions)
    if keys_after != keys:
        _fail("the export unfold/refold round trip changed the folded keys")
    if pose_after != pose:
        _fail("the export unfold/refold round trip changed the pose")

    with rigfold.unfolded_for_export():
        _check("inside export unfold", cases, before,
               [_snapshot(arm, a, f) for a, f in cases], args.tol)
    _check("after refold", cases, before,
           [_snapshot(arm, a, f) for a, f in cases], args.tol)

    unpaired = []
    for bone in arm.data.bones:
        other = rigfold.flip_side_name(bone.name)
        if other and other in arm.data.bones and bone.head_local.x < 0:
            total = rigfold._wrap_angle(rigfold._bone_roll(arm, bone.name)
                                        + rigfold._bone_roll(arm, other))
            if abs(total) > rigfold._MIN_DELTA:
                unpaired.append(bone.name)
    expected = len(warnings)
    if len(unpaired) > expected:
        _fail("%d right-side bones still off Blender's mirror convention "
              "(%d pairs were warned as non-mirror-placed): %s"
              % (len(unpaired), expected, unpaired[:6]))

    if FAILURES:
        print("RIGFOLD-FAILED: %d check(s)" % len(FAILURES))
        sys.exit(1)
    print("RIGFOLD-OK")


if __name__ == "__main__":
    main()
