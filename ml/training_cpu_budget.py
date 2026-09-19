# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Cooperative per-block CPU duty limiting for local training workloads.

The limiter leaves every processor eligible.  A caller places one completed,
synchronous numerical work unit inside ``work_block``.  Every numerical worker
started by that unit must quiesce before the block returns.  The limiter then
rests long enough that measured work divided by measured work plus rest is no
greater than the configured percentage.

This is an average over each completed work/rest cycle.  It is not an
instantaneous utilization limit or a per-one-second guarantee.  Preparation,
evaluation, and other CPU-intensive phases need their own work blocks.  No
unused allowance carries into a later block, so an idle or short block cannot
authorize a future burst.  Unrelated user tasks are outside this cooperative
calculation.  Optional cancellation is checked between rest chunks of at most
250 milliseconds.
"""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import math
import time
from typing import Callable, Iterator


Clock = Callable[[], float]
Sleeper = Callable[[float], None]
CancellationCheck = Callable[[], None]

_MAX_SLEEP_SECONDS = 0.25


@dataclass(frozen=True)
class CpuDutyObservation:
    """Measured timing for one completed synchronous work/rest cycle."""

    work_seconds: float
    rest_seconds: float
    requested_rest_seconds: float

    @property
    def duty_fraction(self) -> float:
        total = self.work_seconds + self.rest_seconds
        return 0.0 if total <= 0.0 else self.work_seconds / total


class TrainingCpuBudget:
    """Sleep after each synchronous work block to enforce a local duty average."""

    def __init__(
        self,
        percent: float = 80.0,
        *,
        clock: Clock = time.perf_counter,
        sleeper: Sleeper = time.sleep,
        cancellation_check: CancellationCheck | None = None,
    ) -> None:
        if (
            isinstance(percent, bool)
            or not isinstance(percent, (int, float))
            or not math.isfinite(percent)
            or percent <= 0.0
            or percent > 80.0
        ):
            raise ValueError("Training CPU percent must be finite and in (0, 80]")
        if (
            not callable(clock)
            or not callable(sleeper)
            or (cancellation_check is not None and not callable(cancellation_check))
        ):
            raise TypeError("Clock, sleeper, and cancellation check must be callable")
        self._fraction = float(percent) / 100.0
        self._clock = clock
        self._sleeper = sleeper
        self._cancellation_check = cancellation_check
        self._active = False
        self._last_observation: CpuDutyObservation | None = None

    @property
    def percent(self) -> float:
        return self._fraction * 100.0

    @property
    def last_observation(self) -> CpuDutyObservation | None:
        return self._last_observation

    @contextmanager
    def work_block(self) -> Iterator[None]:
        """Measure one synchronous work unit and rest before control returns."""
        if self._active:
            raise RuntimeError("Training CPU work blocks cannot overlap or nest")
        started = self._clock()
        if not math.isfinite(started):
            raise RuntimeError("Training CPU clock must be finite and monotonic")
        self._active = True
        try:
            yield
        finally:
            try:
                work_ended = self._clock()
                work_seconds = work_ended - started
                if not math.isfinite(work_seconds) or work_seconds < 0.0:
                    raise RuntimeError("Training CPU clock must be finite and monotonic")
                requested_rest = work_seconds * ((1.0 / self._fraction) - 1.0)
                rest_started = self._clock()
                deadline = rest_started + requested_rest
                if not math.isfinite(rest_started) or not math.isfinite(deadline):
                    raise RuntimeError("Training CPU clock must be finite and monotonic")

                now = rest_started
                while now < deadline:
                    if self._cancellation_check is not None:
                        self._cancellation_check()
                    sleep_seconds = min(_MAX_SLEEP_SECONDS, deadline - now)
                    self._sleeper(sleep_seconds)
                    next_now = self._clock()
                    if not math.isfinite(next_now) or next_now <= now:
                        raise RuntimeError(
                            "Training CPU sleeper returned without advancing the monotonic clock"
                        )
                    now = next_now

                self._last_observation = CpuDutyObservation(
                    work_seconds=work_seconds,
                    rest_seconds=now - rest_started,
                    requested_rest_seconds=requested_rest,
                )
            finally:
                self._active = False


__all__ = ["CpuDutyObservation", "TrainingCpuBudget"]
