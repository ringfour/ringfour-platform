"""Offline tests for ringfour_ui.map — the map renderer.

No browser, no Gradio, no robot. These assert on PIXELS, because the
renderer's whole job is to put the right colour in the right place and a
map that is mirrored, rotated or rescaling looks entirely plausible in a
screenshot.

Three of these defend mistakes that are easy to make and hard to see:
the ROS-to-image y flip, the yaw sign in pixel space, and the two
anti-flicker rules.
"""

from __future__ import annotations

import math

import numpy as np
import pytest

from ringfour.data import OccupancyGrid
from ringfour.models.spatial import Point, Pose
from ringfour_ui.map import (
    FREE,
    NS_PALETTE,
    OCCUPIED,
    UNKNOWN,
    Domain,
    MapView,
    ScalarPoint,
    _Geometry,
    _to_px,
    map_image,
    viridis,
)


def _grid(n: int = 40, fill: int = -1) -> OccupancyGrid:
    return OccupancyGrid(width=n, height=n, resolution=0.05,
                         origin=Pose(x=-1.0, y=-1.0, frame="map"),
                         data=np.full((n, n), fill, np.int8))


def _count(picture: np.ndarray, colour: tuple[int, int, int]) -> int:
    return int((picture == np.array(colour, np.uint8)).all(axis=2).sum())


def _centroid(picture: np.ndarray, colour) -> tuple[float, float]:
    rows, cols = np.nonzero((picture == np.array(colour, np.uint8)).all(axis=2))
    assert rows.size, f"no pixels of colour {colour} were drawn"
    return float(cols.mean()), float(rows.mean())


def _nose(picture: np.ndarray, colour, centre: tuple[float, float]):
    """The drawn pixel furthest from ``centre`` — the triangle's nose.

    Not the centroid: a filled triangle's centroid sits slightly BEHIND
    its origin (the nose is one vertex, the tail is two), so a centroid
    measures the tail and would call a correctly-facing robot backwards.
    """
    rows, cols = np.nonzero((picture == np.array(colour, np.uint8)).all(axis=2))
    assert rows.size, f"no pixels of colour {colour} were drawn"
    far = np.argmax((cols - centre[0]) ** 2 + (rows - centre[1]) ** 2)
    return float(cols[far]), float(rows[far])


# -- the transform -------------------------------------------------------------

def test_to_px_puts_the_map_origin_at_the_bottom_left():
    geo = _Geometry(origin_x=-1.0, origin_y=-2.0, resolution=0.05,
                    width=100, height=80)
    assert _to_px(-1.0, -2.0, geo, scale=1) == (0.0, 80.0)


def test_to_px_scales_with_the_upscale_factor():
    geo = _Geometry(0.0, 0.0, 0.05, 100, 100)
    u1, v1 = _to_px(1.0, 1.0, geo, scale=1)
    u4, v4 = _to_px(1.0, 1.0, geo, scale=4)
    assert (u4, v4) == (u1 * 4, v1 * 4)


def test_the_grid_is_flipped_because_ros_origin_is_bottom_left():
    """A mirrored map looks completely plausible, which is what makes
    this the worst kind of wrong."""
    cells = np.full((20, 20), -1, np.int8)
    cells[0, :] = 0                     # the ROS-bottom row is free
    grid = OccupancyGrid(width=20, height=20, resolution=0.05,
                         origin=Pose(x=0.0, y=0.0, frame="map"), data=cells)
    picture = MapView().render(grid=grid, labels=False, size=20)
    assert (picture[-1, :] == np.array(FREE, np.uint8)).all(), \
        "the free row is not at the bottom of the image"
    assert (picture[0, :] == np.array(UNKNOWN, np.uint8)).all()


def test_occupancy_values_map_to_the_three_surfaces():
    cells = np.full((10, 30), -1, np.int8)
    cells[:, 10:20] = 0
    cells[:, 20:] = 100
    grid = OccupancyGrid(width=30, height=10, resolution=0.05,
                         origin=Pose(x=0.0, y=0.0, frame="map"), data=cells)
    picture = MapView().render(grid=grid, labels=False, size=30)
    assert _count(picture, UNKNOWN) == 100
    assert _count(picture, FREE) == 100
    assert _count(picture, OCCUPIED) == 100


# -- poses ---------------------------------------------------------------------

def test_a_pose_at_yaw_zero_points_along_world_x():
    """World +x is image +u. Getting the yaw sign wrong yields a robot
    that drives sideways on screen while behaving correctly."""
    view = MapView()
    picture = view.render(
        grid=_grid(), labels=False,
        poses={"robot": Pose(x=0.0, y=0.0, yaw=0.0, frame="map")}, size=400)
    geo = _Geometry(-1.0, -1.0, 0.05, 40, 40)
    scale = 10
    centre = _to_px(0.0, 0.0, geo, scale)
    nose_u, nose_v = _nose(picture, NS_PALETTE[0], centre)
    assert nose_u > centre[0], "the triangle does not point towards +x"
    assert abs(nose_v - centre[1]) < scale, "it drifted in y at yaw 0"


def test_a_pose_at_yaw_ninety_points_up_the_image():
    """World +y is image UP, because of the flip."""
    view = MapView()
    picture = view.render(
        grid=_grid(), labels=False,
        poses={"robot": Pose(x=0.0, y=0.0, yaw=math.pi / 2, frame="map")},
        size=400)
    geo = _Geometry(-1.0, -1.0, 0.05, 40, 40)
    centre = _to_px(0.0, 0.0, geo, 10)
    nose_u, nose_v = _nose(picture, NS_PALETTE[0], centre)
    assert nose_v < centre[1], "world +y did not render upward"
    assert abs(nose_u - centre[0]) < 10


def test_each_namespace_gets_its_own_fixed_colour():
    """What makes a multi-robot map readable."""
    picture = MapView().render(
        grid=_grid(), labels=False,
        poses={"picker": Pose(x=-0.5, y=0.0, frame="map"),
               "spotter": Pose(x=0.5, y=0.0, frame="map")}, size=400)
    assert _count(picture, NS_PALETTE[0]) > 0
    assert _count(picture, NS_PALETTE[1]) > 0


# -- points --------------------------------------------------------------------

def test_a_scalar_point_takes_its_colour_from_the_ramp():
    """The measurement overlay: the reason this module exists."""
    view = MapView()
    picture = view.render(
        grid=_grid(), labels=False, size=400,
        points=[ScalarPoint(Point(x=-0.5, y=0.0, frame="map"), 6.0, "pH"),
                ScalarPoint(Point(x=0.5, y=0.0, frame="map"), 8.0, "pH")])
    low = viridis(view.domain.normalise(6.0))
    high = viridis(view.domain.normalise(8.0))
    assert _count(picture, low) > 0
    assert _count(picture, high) > 0
    assert low != high


def test_a_plain_point_is_not_coloured_by_the_ramp():
    view = MapView()
    picture = view.render(grid=_grid(), labels=False, size=400,
                          points=[Point(x=0.0, y=0.0, frame="map")])
    assert not view.domain.initialised, "a plain point moved the colour domain"
    assert _count(picture, viridis(0.0)) == 0


def test_plain_and_scalar_points_mix_in_one_call():
    """Waypoints under a measured field is the normal case, not an edge."""
    view = MapView()
    view.render(grid=_grid(), labels=False, size=400, points=[
        Point(x=-0.5, y=0.0, frame="map"),
        ScalarPoint(Point(x=0.5, y=0.0, frame="map"), 7.0, "pH"),
        {"point": Point(x=0.0, y=0.5, frame="map"), "value": 7.4,
         "label": "pH"},
    ])
    assert view.domain.initialised


# -- the two anti-flicker rules ------------------------------------------------

def test_the_colour_domain_does_not_move_for_a_value_inside_it():
    """Without this the legend rescales every frame and a colour stops
    meaning anything."""
    domain = Domain()
    domain.observe([6.0, 8.0])
    before = (domain.low, domain.high)
    assert domain.observe([7.0]) is False
    assert (domain.low, domain.high) == before


def test_the_colour_domain_expands_for_a_value_outside_it():
    domain = Domain()
    domain.observe([6.0, 8.0])
    assert domain.observe([20.0]) is True
    assert domain.high > 20.0


def test_a_single_sample_does_not_give_a_zero_width_domain():
    domain = Domain()
    domain.observe([7.0])
    assert domain.high > domain.low
    assert 0.0 <= domain.normalise(7.0) <= 1.0


def test_the_fitted_view_does_not_move_for_a_point_inside_it():
    """With no grid, a view that refits every frame makes a moving robot
    look stationary while the world slides around it."""
    view = MapView()
    first = view.render(labels=False, points=[
        Point(x=0.0, y=0.0, frame="map"), Point(x=10.0, y=10.0, frame="map")])
    second = view.render(labels=False, points=[
        Point(x=0.0, y=0.0, frame="map"), Point(x=10.0, y=10.0, frame="map"),
        Point(x=5.0, y=5.0, frame="map")])
    assert first.shape == second.shape


def test_the_fitted_view_grows_for_a_point_outside_it():
    view = MapView()
    view.render(labels=False, points=[Point(x=0.0, y=0.0, frame="map"),
                                      Point(x=1.0, y=1.0, frame="map")])
    before = view._fit
    view.render(labels=False, points=[Point(x=0.0, y=0.0, frame="map"),
                                      Point(x=50.0, y=50.0, frame="map")])
    assert view._fit is not before


# -- the ramp ------------------------------------------------------------------

def test_the_ramp_is_clamped_not_wrapped():
    assert viridis(-5.0) == viridis(0.0)
    assert viridis(5.0) == viridis(1.0)
    assert viridis(float("nan")) == viridis(0.0)


# -- degenerate input ----------------------------------------------------------

def test_rendering_nothing_at_all_is_a_blank_frame_not_an_exception():
    """A panel that has not been updated yet is a normal state and must
    look like one."""
    picture = MapView().render()
    assert picture.ndim == 3 and picture.shape[2] == 3
    assert picture.dtype == np.uint8


def test_a_path_of_one_point_does_not_raise():
    MapView().render(grid=_grid(),
                     path=[Point(x=0.0, y=0.0, frame="map")])


def test_map_image_renders_without_a_view_of_your_own():
    assert map_image(grid=_grid()).shape[2] == 3


def test_two_views_keep_independent_colour_domains():
    """Two maps showing different fields must not fight over the ramp."""
    ph, clearance = MapView(), MapView()
    ph.render(grid=_grid(), points=[
        ScalarPoint(Point(x=0.0, y=0.0, frame="map"), 7.0, "pH")])
    clearance.render(grid=_grid(), points=[
        ScalarPoint(Point(x=0.0, y=0.0, frame="map"), 250.0, "mm")])
    assert ph.domain.high < clearance.domain.low
