"""Short display tails for bones the GLTF importer draws far too long.

The importer points each bone's tail at its child, and a bone without a usable
child takes its parent's length. Game rigs hold locators and pivot nodes far
from the body (pc02's head_pointShape sits 13.8 units to the side and its child
head translates back), so those bones and their parents are drawn several
times the character's width. trim_tails() shortens them along their own
direction, which leaves every bone's head and orientation, and so the skin,
where it was.

A tail edit also nudges the rest matrix in its last bits, and cooked HDB/MPK
bind data must match the game bit for bit. full_tails_for_export() puts the
original tails back around a scene export.
"""
import json

import bpy

from contextlib import contextmanager
from mathutils import Vector

from .rigfold import edit_bones

# Armature-data property holding {bone_name: [original_tail, trimmed_tail]}.
TRIM_PROP = "sf_tail_trim"

# Bones longer than this fraction of the model's largest mesh dimension are
# trimmed. Real limbs stay under about 0.3; above 0.4 are the root, hips,
# locators and prop pivots.
_MAX_FRACTION = 0.4


def plan_trims(lengths, model_size):
    """Chooses new lengths for overlong bones.

    lengths: {bone_name: length}. Returns {bone_name: new_length}, where each
    bone longer than _MAX_FRACTION of model_size gets the rig's median bone
    length."""
    if not lengths or model_size <= 0:
        return {}
    ordered = sorted(lengths.values())
    median = ordered[len(ordered) // 2]
    limit = _MAX_FRACTION * model_size
    return {name: median for name, length in lengths.items() if length > limit}


def model_size(objs):
    """Largest world-space bounding-box dimension of the mesh objects."""
    points = [o.matrix_world @ Vector(corner)
              for o in objs if o.type == 'MESH' for corner in o.bound_box]
    if not points:
        return 0.0
    return max(max(p[i] for p in points) - min(p[i] for p in points) for i in range(3))


def _trim_state(arm_obj):
    raw = arm_obj.data.get(TRIM_PROP)
    return json.loads(raw) if raw else {}


def trim_tails(arm_obj, size):
    """Shortens the armature's overlong bones and records their original
    tails. Returns the trimmed bone names."""
    plan = plan_trims({b.name: b.length for b in arm_obj.data.bones}, size)
    if not plan:
        return []
    state = {}
    with edit_bones(arm_obj) as ebs:
        for name, length in plan.items():
            eb = ebs[name]
            original = list(eb.tail)
            eb.length = length
            state[name] = [original, list(eb.tail)]
    arm_obj.data[TRIM_PROP] = json.dumps(state)
    return sorted(plan)


def _set_tails(arm_obj, state, from_index, to_index):
    """Moves each recorded tail from one end of its [original, trimmed] pair
    to the other. Tails the user has moved since are left alone."""
    with edit_bones(arm_obj) as ebs:
        for name, tails in state.items():
            eb = ebs.get(name)
            if eb is not None and list(eb.tail) == tails[from_index]:
                eb.tail = tails[to_index]


@contextmanager
def full_tails_for_export():
    """Restores original tails around an export, then trims them again."""
    trimmed = [(o, _trim_state(o)) for o in bpy.data.objects
               if o.type == 'ARMATURE' and _trim_state(o)]
    for arm_obj, state in trimmed:
        _set_tails(arm_obj, state, 1, 0)
    try:
        yield
    finally:
        for arm_obj, state in trimmed:
            _set_tails(arm_obj, state, 0, 1)
