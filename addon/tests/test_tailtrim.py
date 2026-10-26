"""Tests for tailtrim's trim planning. Edit-bone changes and the export
restore need Blender and are checked by smoke/headless_tailtrim.py."""
import importlib
import sys
import types

import pytest


@pytest.fixture
def tailtrim(monkeypatch, stub_mathutils):
    monkeypatch.setitem(sys.modules, 'bpy', types.ModuleType('bpy'))
    import io_shadowforge.rigfold
    importlib.reload(io_shadowforge.rigfold)
    import io_shadowforge.tailtrim
    return importlib.reload(io_shadowforge.tailtrim)


def test_plan_trims_pc02(tailtrim):
    # pc02 (Jiro) after a FORTUNE import: mesh size 17.0, median bone 0.634.
    lengths = {
        "referenceShape": 9.582,
        "hips": 9.582,
        "neck": 13.806,
        "head_pointShape": 13.766,
        "leftupleg": 4.1,
        "spine": 0.634,
        "head": 0.5,
    }
    lengths.update({"finger%d" % i: 0.3 for i in range(5)})
    plan = tailtrim.plan_trims(lengths, 17.0)
    assert set(plan) == {"referenceShape", "hips", "neck", "head_pointShape"}
    assert all(length == 0.634 for length in plan.values())


def test_plan_trims_keeps_limbs(tailtrim):
    # np209 (King Jibral): the longest limb is 0.13 of the model.
    lengths = {"spine": 6.191, "rightarm": 5.661, "tail": 2.768, "tin": 1.679}
    assert tailtrim.plan_trims(lengths, 47.29) == {}


def test_plan_trims_without_meshes(tailtrim):
    assert tailtrim.plan_trims({"referenceShape": 9.5}, 0.0) == {}
    assert tailtrim.plan_trims({}, 10.0) == {}
