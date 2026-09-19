# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import time

import pytest

from ml.training_cpu_budget import TrainingCpuBudget


class FakeTime:
    def __init__(self) -> None:
        self.now = 100.0
        self.sleeps: list[float] = []

    def clock(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.advance(seconds)


@pytest.mark.parametrize("percent", [False, 0, -1, 80.01, float("inf"), float("nan")])
def test_percent_must_be_finite_and_in_supported_range(percent) -> None:
    with pytest.raises(ValueError):
        TrainingCpuBudget(percent)


def test_completed_block_rests_to_requested_average() -> None:
    fake = FakeTime()
    limiter = TrainingCpuBudget(80, clock=fake.clock, sleeper=fake.sleep)
    with limiter.work_block():
        fake.advance(4.0)

    observation = limiter.last_observation
    assert observation is not None
    assert fake.sleeps == pytest.approx([0.25, 0.25, 0.25, 0.25])
    assert all(seconds <= 0.25 for seconds in fake.sleeps)
    assert observation.work_seconds == pytest.approx(4.0)
    assert observation.rest_seconds == pytest.approx(1.0)
    assert observation.duty_fraction == pytest.approx(0.8)


def test_each_block_has_an_independent_budget_without_banked_idle_time() -> None:
    fake = FakeTime()
    limiter = TrainingCpuBudget(40, clock=fake.clock, sleeper=fake.sleep)
    with limiter.work_block():
        fake.advance(0.0)
    with limiter.work_block():
        fake.advance(2.0)

    assert fake.sleeps == pytest.approx([0.25] * 12)
    assert limiter.last_observation is not None
    assert limiter.last_observation.duty_fraction == pytest.approx(0.4)


def test_nested_blocks_and_short_sleep_fail_closed_and_reset_active_state() -> None:
    fake = FakeTime()
    limiter = TrainingCpuBudget(80, clock=fake.clock, sleeper=lambda _: None)
    with pytest.raises(RuntimeError, match="overlap or nest"):
        with limiter.work_block():
            with limiter.work_block():
                pass

    limiter = TrainingCpuBudget(80, clock=fake.clock, sleeper=lambda _: None)
    with pytest.raises(RuntimeError, match="without advancing"):
        with limiter.work_block():
            fake.advance(1.0)
    with limiter.work_block():
        pass


def test_sleeper_exception_resets_active_state() -> None:
    fake = FakeTime()

    def raising_sleep(_: float) -> None:
        raise RuntimeError("sleep failed")

    limiter = TrainingCpuBudget(80, clock=fake.clock, sleeper=raising_sleep)
    with pytest.raises(RuntimeError, match="sleep failed"):
        with limiter.work_block():
            fake.advance(1.0)
    with limiter.work_block():
        pass


def test_cancellation_is_checked_between_sleep_chunks_and_resets_active_state() -> None:
    fake = FakeTime()
    should_cancel = True
    checks = 0

    def check_cancellation() -> None:
        nonlocal checks, should_cancel
        checks += 1
        if should_cancel and checks == 3:
            raise RuntimeError("cancelled")

    limiter = TrainingCpuBudget(
        80,
        clock=fake.clock,
        sleeper=fake.sleep,
        cancellation_check=check_cancellation,
    )
    with pytest.raises(RuntimeError, match="cancelled"):
        with limiter.work_block():
            fake.advance(4.0)

    assert fake.sleeps == pytest.approx([0.25, 0.25])
    assert limiter.last_observation is None

    should_cancel = False
    with limiter.work_block():
        fake.advance(0.4)
    assert limiter.last_observation is not None


def test_short_real_cpu_workload_completes_under_one_second() -> None:
    limiter = TrainingCpuBudget(80)
    wall_started = time.perf_counter()
    with limiter.work_block():
        work_started = time.perf_counter()
        value = 0
        while time.perf_counter() - work_started < 0.02:
            value = (value * 33 + 17) % 1_000_003
    wall_elapsed = time.perf_counter() - wall_started

    observation = limiter.last_observation
    assert value >= 0
    assert observation is not None
    assert observation.work_seconds > 0.0
    assert observation.duty_fraction <= 0.8
    assert wall_elapsed < 1.0
