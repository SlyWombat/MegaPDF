#!/usr/bin/env python3
"""Real touch events, for the two gestures `adb shell input` cannot make.

`input tap` starts a JVM per call — 200 ms and up on a loaded host — so two of
them are never inside the 300 ms double-tap window, and `input` has one pointer,
so it cannot pinch at all. Driving the app's zoom therefore needs the kernel
input device: `sendevent` is a small native binary, and a whole gesture goes in
one `adb shell` batch.

The emulator's touchscreen is `virtio_input_multi_touch_1`, MT protocol B with
eleven slots and an axis range of 0..32767 across the display.

    from touch import Touch
    Touch(device).double_tap(x, y)
    Touch(device).pinch(cx, cy, from_gap=120, to_gap=900)
"""
from __future__ import annotations

import time

# linux/input-event-codes.h
EV_SYN, EV_KEY, EV_ABS = 0, 1, 3
SYN_REPORT = 0
BTN_TOUCH = 0x14B
ABS_MT_SLOT = 0x2F
ABS_MT_POSITION_X = 0x35
ABS_MT_POSITION_Y = 0x36
ABS_MT_TRACKING_ID = 0x39
ABS_MT_TOOL_TYPE = 0x37
ABS_MT_PRESSURE = 0x3A
MT_TOOL_FINGER = 0

AXIS_MAX = 32767


class Touch:
    def __init__(self, device, node: str = "/dev/input/event2"):
        self.d = device
        self.node = node
        self._size: tuple[int, int] | None = None

    # --- plumbing ----------------------------------------------------------

    @property
    def size(self) -> tuple[int, int]:
        if self._size is None:
            self._size = self.d.screen_size()
        return self._size

    def _scale(self, x: float, y: float) -> tuple[int, int]:
        w, h = self.size
        return (max(0, min(AXIS_MAX, round(x / w * AXIS_MAX))),
                max(0, min(AXIS_MAX, round(y / h * AXIS_MAX))))

    def _ev(self, type_: int, code: int, value: int) -> str:
        return f"sendevent {self.node} {type_} {code} {value}"

    def _send(self, lines: list[str]) -> None:
        # One batch, so the whole gesture lands inside the timeouts it has to beat.
        self.d.shell(";".join(lines))

    def _sync(self) -> str:
        return self._ev(EV_SYN, SYN_REPORT, 0)

    def _finger_down(self, slot: int, tracking: int, x: float, y: float) -> list[str]:
        ax, ay = self._scale(x, y)
        out = [self._ev(EV_ABS, ABS_MT_SLOT, slot),
               self._ev(EV_ABS, ABS_MT_TRACKING_ID, tracking),
               # The emulator's touchscreen advertises BTN_TOOL_PEN, so a contact
               # that does not say what it is gets classified as a stylus — which
               # is enough to bring up Gboard's "Try out your stylus" sheet over
               # the app mid-run. Say finger.
               self._ev(EV_ABS, ABS_MT_TOOL_TYPE, MT_TOOL_FINGER),
               self._ev(EV_ABS, ABS_MT_POSITION_X, ax),
               self._ev(EV_ABS, ABS_MT_POSITION_Y, ay),
               self._ev(EV_ABS, ABS_MT_PRESSURE, 100)]
        if slot == 0:
            out.append(self._ev(EV_KEY, BTN_TOUCH, 1))
        return out

    def _finger_move(self, slot: int, x: float, y: float) -> list[str]:
        ax, ay = self._scale(x, y)
        return [self._ev(EV_ABS, ABS_MT_SLOT, slot),
                self._ev(EV_ABS, ABS_MT_POSITION_X, ax),
                self._ev(EV_ABS, ABS_MT_POSITION_Y, ay)]

    def _finger_up(self, slot: int) -> list[str]:
        return [self._ev(EV_ABS, ABS_MT_SLOT, slot),
                self._ev(EV_ABS, ABS_MT_TRACKING_ID, -1)]

    # --- gestures ----------------------------------------------------------

    def tap(self, x: float, y: float, settle: float = 0.6) -> None:
        self._send(self._finger_down(0, 1, x, y) + [self._sync()]
                   + self._finger_up(0) + [self._ev(EV_KEY, BTN_TOUCH, 0), self._sync()])
        time.sleep(settle)

    def double_tap(self, x: float, y: float, gap: float = 0.09,
                   settle: float = 1.5) -> None:
        """Two taps in one batch, well inside ViewConfiguration's 300 ms window."""
        lines = (self._finger_down(0, 1, x, y) + [self._sync()]
                 + self._finger_up(0) + [self._ev(EV_KEY, BTN_TOUCH, 0), self._sync()]
                 + [f"sleep {gap}"]
                 + self._finger_down(0, 2, x, y) + [self._sync()]
                 + self._finger_up(0) + [self._ev(EV_KEY, BTN_TOUCH, 0), self._sync()])
        self._send(lines)
        time.sleep(settle)

    def pinch(self, cx: float, cy: float, from_gap: float, to_gap: float,
              steps: int = 12, settle: float = 1.5) -> None:
        """Two fingers on a horizontal line through (cx, cy), moving apart or together."""
        lines = (self._finger_down(0, 10, cx - from_gap / 2, cy)
                 + self._finger_down(1, 11, cx + from_gap / 2, cy)
                 + [self._sync()])
        for step in range(1, steps + 1):
            gap = from_gap + (to_gap - from_gap) * step / steps
            lines += (self._finger_move(0, cx - gap / 2, cy)
                      + self._finger_move(1, cx + gap / 2, cy)
                      + [self._sync(), "sleep 0.02"])
        lines += (self._finger_up(0) + self._finger_up(1)
                  + [self._ev(EV_KEY, BTN_TOUCH, 0), self._sync()])
        self._send(lines)
        time.sleep(settle)
