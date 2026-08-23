import math
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox

from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

import openvr


# --------------------
# Defaults
# --------------------

DEFAULT_OSC_PORT = 9135

HEIGHT_DEADBAND_METERS = 0.005
MAX_COMPENSATION_METERS = 2.0
AUTO_REBASE_ON_AVATAR_CHANGE = True


# --------------------
# SteamVR playspace helper
# --------------------

class SteamVRPlayspace:
    def __init__(self):
        self.vr_system = None
        self.chap = None

        # Stored internally as Python list:
        # [
        #   [m00, m01, m02, m03],
        #   [m10, m11, m12, m13],
        #   [m20, m21, m22, m23],
        # ]
        self.original_matrix = None
        self.base_matrix = None
        self.last_applied_offset = 0.0

        self.lock = threading.RLock()

    def start(self):
        with self.lock:
            if self.vr_system is not None:
                return

            self.vr_system = openvr.init(openvr.VRApplication_Utility)
            self.chap = openvr.VRChaperoneSetup()

            self.original_matrix = self._read_current_matrix()
            self.base_matrix = self._copy_matrix(self.original_matrix)
            self.last_applied_offset = 0.0

    def shutdown(self):
        with self.lock:
            try:
                self.reset_offsets_added_by_app()
            except Exception:
                pass

            if self.vr_system is not None:
                openvr.shutdown()

            self.vr_system = None
            self.chap = None

    def _call(self, obj, lower_name, *args):
        fn = getattr(obj, lower_name, None)

        if fn is None:
            pascal = lower_name[0].upper() + lower_name[1:]
            fn = getattr(obj, pascal, None)

        if fn is None:
            raise AttributeError(f"OpenVR object has no method {lower_name}")

        return fn(*args)

    def _live_config_value(self):
        return getattr(openvr, "EChaperoneConfigFile_Live", 1)

    def _matrix_to_list(self, src):
        """
        Convert whatever pyopenvr gives us into a normal Python 3x4 list.

        Handles:
        - tuple/list 3x4
        - tuple/list flat 12
        - object.m[3][4]
        - object.m[12]
        - object.m0...object.m11
        """

        # Case 1: tuple/list 3x4
        if isinstance(src, (tuple, list)):
            if len(src) == 3 and all(isinstance(row, (tuple, list)) and len(row) == 4 for row in src):
                return [
                    [float(src[0][0]), float(src[0][1]), float(src[0][2]), float(src[0][3])],
                    [float(src[1][0]), float(src[1][1]), float(src[1][2]), float(src[1][3])],
                    [float(src[2][0]), float(src[2][1]), float(src[2][2]), float(src[2][3])],
                ]

            # Case 2: tuple/list flat 12
            if len(src) == 12:
                return [
                    [float(src[0]), float(src[1]), float(src[2]), float(src[3])],
                    [float(src[4]), float(src[5]), float(src[6]), float(src[7])],
                    [float(src[8]), float(src[9]), float(src[10]), float(src[11])],
                ]

        # Case 3: object.m[3][4]
        try:
            return [
                [float(src.m[0][0]), float(src.m[0][1]), float(src.m[0][2]), float(src.m[0][3])],
                [float(src.m[1][0]), float(src.m[1][1]), float(src.m[1][2]), float(src.m[1][3])],
                [float(src.m[2][0]), float(src.m[2][1]), float(src.m[2][2]), float(src.m[2][3])],
            ]
        except Exception:
            pass

        # Case 4: object.m flat 12
        try:
            return [
                [float(src.m[0]), float(src.m[1]), float(src.m[2]), float(src.m[3])],
                [float(src.m[4]), float(src.m[5]), float(src.m[6]), float(src.m[7])],
                [float(src.m[8]), float(src.m[9]), float(src.m[10]), float(src.m[11])],
            ]
        except Exception:
            pass

        # Case 5: object.m0...object.m11
        try:
            vals = [float(getattr(src, f"m{i}")) for i in range(12)]
            return [
                [vals[0], vals[1], vals[2], vals[3]],
                [vals[4], vals[5], vals[6], vals[7]],
                [vals[8], vals[9], vals[10], vals[11]],
            ]
        except Exception:
            pass

        raise RuntimeError(f"Unsupported matrix format from pyopenvr: {type(src)}")

    def _list_to_hmd_matrix(self, mat):
        """
        Convert Python 3x4 list back to openvr.HmdMatrix34_t.
        """
        m = openvr.HmdMatrix34_t()

        # Most pyopenvr versions support m.m[row][col].
        try:
            for r in range(3):
                for c in range(4):
                    m.m[r][c] = float(mat[r][c])
            return m
        except Exception:
            pass

        # Some may expose flat m[12].
        try:
            flat = [
                mat[0][0], mat[0][1], mat[0][2], mat[0][3],
                mat[1][0], mat[1][1], mat[1][2], mat[1][3],
                mat[2][0], mat[2][1], mat[2][2], mat[2][3],
            ]

            for i in range(12):
                m.m[i] = float(flat[i])

            return m
        except Exception:
            pass

        # Some may expose m0...m11.
        try:
            flat = [
                mat[0][0], mat[0][1], mat[0][2], mat[0][3],
                mat[1][0], mat[1][1], mat[1][2], mat[1][3],
                mat[2][0], mat[2][1], mat[2][2], mat[2][3],
            ]

            for i in range(12):
                setattr(m, f"m{i}", float(flat[i]))

            return m
        except Exception:
            pass

        raise RuntimeError("Could not convert matrix to openvr.HmdMatrix34_t")

    def _copy_matrix(self, mat):
        return [
            [float(mat[0][0]), float(mat[0][1]), float(mat[0][2]), float(mat[0][3])],
            [float(mat[1][0]), float(mat[1][1]), float(mat[1][2]), float(mat[1][3])],
            [float(mat[2][0]), float(mat[2][1]), float(mat[2][2]), float(mat[2][3])],
        ]

    def _read_current_matrix(self):
        if self.chap is None:
            raise RuntimeError("SteamVR chaperone setup is not initialized.")

        self._call(self.chap, "revertWorkingCopy")

        # pyopenvr on your machine returns the matrix directly.
        # Other versions use an output argument.
        try:
            result = self._call(
                self.chap,
                "getWorkingStandingZeroPoseToRawTrackingPose"
            )

            if result is None:
                raise RuntimeError(
                    "getWorkingStandingZeroPoseToRawTrackingPose returned None."
                )

            return self._matrix_to_list(result)

        except TypeError:
            raw = openvr.HmdMatrix34_t()

            ok = self._call(
                self.chap,
                "getWorkingStandingZeroPoseToRawTrackingPose",
                raw
            )

            if ok is False:
                raise RuntimeError(
                    "GetWorkingStandingZeroPoseToRawTrackingPose failed."
                )

            return self._matrix_to_list(raw)

    def _commit_matrix(self, mat):
        if self.chap is None:
            raise RuntimeError("SteamVR chaperone setup is not initialized.")

        hmd_matrix = self._list_to_hmd_matrix(mat)

        self._call(self.chap, "revertWorkingCopy")

        self._call(
            self.chap,
            "setWorkingStandingZeroPoseToRawTrackingPose",
            hmd_matrix
        )

        try:
            ok = self._call(
                self.chap,
                "commitWorkingCopy",
                self._live_config_value()
            )
        except TypeError:
            ok = self._call(
                self.chap,
                "commitWorkingCopy",
                int(self._live_config_value())
            )

        if ok is False:
            raise RuntimeError("CommitWorkingCopy failed.")

    def capture_current_as_base(self):
        with self.lock:
            self.base_matrix = self._read_current_matrix()
            self.last_applied_offset = 0.0

    def apply_y_offset_from_base(self, y_offset):
        with self.lock:
            if self.base_matrix is None:
                self.capture_current_as_base()

            y_offset = max(
                -MAX_COMPENSATION_METERS,
                min(MAX_COMPENSATION_METERS, float(y_offset))
            )

            if abs(y_offset - self.last_applied_offset) < HEIGHT_DEADBAND_METERS:
                return self.last_applied_offset

            mat = self._copy_matrix(self.base_matrix)

            # Matrix translation:
            # mat[0][3] = X
            # mat[1][3] = Y
            # mat[2][3] = Z
            mat[1][3] = self.base_matrix[1][3] + y_offset

            self._commit_matrix(mat)
            self.last_applied_offset = y_offset

            return y_offset

    def reset_offsets_added_by_app(self):
        with self.lock:
            if self.original_matrix is None:
                return

            self._commit_matrix(self.original_matrix)
            self.base_matrix = self._copy_matrix(self.original_matrix)
            self.last_applied_offset = 0.0


# --------------------
# UI app
# --------------------

class EyeHeightLockApp:
    def __init__(self, root):
        self.root = root
        self.root.title("VRChat Eye Height Lock")
        self.root.geometry("470x350")
        self.root.resizable(False, False)

        self.vr = SteamVRPlayspace()

        self.server = None
        self.server_thread = None
        self.running = False

        self.ui_queue = queue.Queue()

        self.last_eye_height = None
        self.base_eye_height = None
        self.need_baseline = True

        self.enabled_flag = True
        self.invert_direction_flag = False

        self.port_var = tk.StringVar(value=str(DEFAULT_OSC_PORT))
        self.enabled_var = tk.BooleanVar(value=True)
        self.invert_var = tk.BooleanVar(value=False)

        self.status_var = tk.StringVar(value="Stopped.")
        self.eye_var = tk.StringVar(value="Current eye height: --")
        self.base_var = tk.StringVar(value="Baseline eye height: --")
        self.offset_var = tk.StringVar(value="Applied offset: 0.0000 m")

        self._build_ui()

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.after(50, self._process_ui_queue)

    def _build_ui(self):
        pad = {"padx": 10, "pady": 5}

        main = ttk.Frame(self.root)
        main.pack(fill="both", expand=True, padx=10, pady=10)

        port_frame = ttk.Frame(main)
        port_frame.pack(fill="x", **pad)

        ttk.Label(port_frame, text="VRChat OSC listen port:").pack(side="left")

        port_entry = ttk.Entry(port_frame, textvariable=self.port_var, width=8)
        port_entry.pack(side="left", padx=8)

        self.start_button = ttk.Button(
            port_frame,
            text="Start",
            command=self.toggle_start_stop
        )
        self.start_button.pack(side="right")

        ttk.Checkbutton(
            main,
            text="Enable compensation",
            variable=self.enabled_var,
            command=self.on_enabled_changed
        ).pack(anchor="w", **pad)

        ttk.Checkbutton(
            main,
            text="Invert direction if movement is backwards",
            variable=self.invert_var,
            command=self.on_invert_changed
        ).pack(anchor="w", **pad)

        ttk.Separator(main).pack(fill="x", pady=8)

        ttk.Label(main, textvariable=self.eye_var).pack(anchor="w", **pad)
        ttk.Label(main, textvariable=self.base_var).pack(anchor="w", **pad)
        ttk.Label(main, textvariable=self.offset_var).pack(anchor="w", **pad)

        ttk.Separator(main).pack(fill="x", pady=8)

        buttons = ttk.Frame(main)
        buttons.pack(fill="x", **pad)

        ttk.Button(
            buttons,
            text="Re-baseline current height",
            command=self.rebaseline_now
        ).pack(side="left", expand=True, fill="x", padx=(0, 5))

        ttk.Button(
            buttons,
            text="Reset offsets added by app",
            command=self.reset_offsets
        ).pack(side="left", expand=True, fill="x", padx=(5, 0))

        ttk.Separator(main).pack(fill="x", pady=8)

        ttk.Label(
            main,
            textvariable=self.status_var,
            wraplength=430
        ).pack(anchor="w", **pad)

    def _set_status_threadsafe(self, text):
        self.ui_queue.put(("status", text))

    def _set_vars_threadsafe(self, eye=None, base=None, offset=None):
        self.ui_queue.put(("vars", eye, base, offset))

    def _process_ui_queue(self):
        try:
            while True:
                item = self.ui_queue.get_nowait()

                if item[0] == "status":
                    self.status_var.set(item[1])

                elif item[0] == "vars":
                    _, eye, base, offset = item

                    if eye is not None:
                        self.eye_var.set(f"Current eye height: {eye:.4f} m")

                    if base is not None:
                        self.base_var.set(f"Baseline eye height: {base:.4f} m")

                    if offset is not None:
                        self.offset_var.set(f"Applied offset: {offset:.4f} m")

        except queue.Empty:
            pass

        self.root.after(50, self._process_ui_queue)

    def on_enabled_changed(self):
        self.enabled_flag = bool(self.enabled_var.get())

        if self.enabled_flag:
            self.status_var.set("Compensation enabled.")
        else:
            self.status_var.set(
                "Compensation disabled. Current offset is left as-is. "
                "Use Reset to remove offsets added by app."
            )

    def on_invert_changed(self):
        self.invert_direction_flag = bool(self.invert_var.get())
        self.status_var.set("Direction setting changed.")

    def toggle_start_stop(self):
        if self.running:
            self.stop()
        else:
            self.start()

    def start(self):
        try:
            port = int(self.port_var.get().strip())
            if port < 1 or port > 65535:
                raise ValueError
        except ValueError:
            messagebox.showerror("Invalid port", "Port must be a number from 1 to 65535.")
            return

        try:
            self.vr.start()
        except Exception as e:
            messagebox.showerror(
                "SteamVR error",
                f"Could not initialize SteamVR/OpenVR.\n\n"
                f"Make sure SteamVR is running.\n\n{e}"
            )
            return

        dispatcher = Dispatcher()
        dispatcher.map("/avatar/parameters/EyeHeightAsMeters", self.on_eye_height_osc)
        dispatcher.map("/avatar/change", self.on_avatar_change_osc)

        try:
            self.server = ThreadingOSCUDPServer(("127.0.0.1", port), dispatcher)
        except Exception as e:
            messagebox.showerror(
                "OSC error",
                f"Could not listen on UDP port {port}.\n\n"
                f"Is another OSC program already using that port?\n\n{e}"
            )
            return

        self.running = True
        self.need_baseline = True
        self.last_eye_height = None
        self.base_eye_height = None

        self.server_thread = threading.Thread(
            target=self.server.serve_forever,
            daemon=True
        )
        self.server_thread.start()

        self.start_button.config(text="Stop")
        self.status_var.set(
            f"Running. Listening for VRChat OSC on 127.0.0.1:{port}. "
            f"Waiting for EyeHeightAsMeters..."
        )

    def stop(self):
        self.running = False

        if self.server is not None:
            try:
                self.server.shutdown()
                self.server.server_close()
            except Exception:
                pass

        self.server = None
        self.server_thread = None

        try:
            self.vr.reset_offsets_added_by_app()
            self._set_vars_threadsafe(offset=0.0)
            self.status_var.set("Stopped. Offsets added by app were reset.")
        except Exception as e:
            self.status_var.set(f"Stopped, but reset failed: {e}")

        self.start_button.config(text="Start")

    def on_eye_height_osc(self, address, *args):
        if not args:
            return

        try:
            eye = float(args[0])
        except Exception:
            return

        if not math.isfinite(eye):
            return

        if eye <= 0.01 or eye > 100.0:
            return

        self.last_eye_height = eye

        if self.need_baseline or self.base_eye_height is None:
            try:
                self.vr.capture_current_as_base()
                self.base_eye_height = eye
                self.need_baseline = False

                self._set_vars_threadsafe(eye=eye, base=eye, offset=0.0)
                self._set_status_threadsafe(
                    f"Baseline captured: {eye:.4f} m. Compensation ready."
                )
            except Exception as e:
                self._set_status_threadsafe(f"Baseline failed: {e}")
            return

        self._set_vars_threadsafe(eye=eye)

        if not self.enabled_flag:
            return

        delta = eye - self.base_eye_height

        # Default:
        # avatar eye height increases -> move playspace opposite Y.
        direction = 1.0 if self.invert_direction_flag else -1.0
        y_offset = direction * delta

        try:
            applied = self.vr.apply_y_offset_from_base(y_offset)
            self._set_vars_threadsafe(offset=applied)
        except Exception as e:
            self._set_status_threadsafe(f"Apply offset failed: {e}")

    def on_avatar_change_osc(self, address, *args):
        if AUTO_REBASE_ON_AVATAR_CHANGE:
            self.need_baseline = True
            self.base_eye_height = None
            self._set_status_threadsafe(
                "Avatar changed. Will re-baseline on next EyeHeightAsMeters."
            )

    def rebaseline_now(self):
        if not self.running:
            self.status_var.set("Start the app first.")
            return

        if self.last_eye_height is None:
            self.need_baseline = True
            self.status_var.set(
                "No EyeHeightAsMeters received yet. "
                "Will baseline when VRChat sends one."
            )
            return

        try:
            self.vr.capture_current_as_base()
            self.base_eye_height = self.last_eye_height
            self.need_baseline = False
            self.vr.last_applied_offset = 0.0

            self.base_var.set(f"Baseline eye height: {self.base_eye_height:.4f} m")
            self.offset_var.set("Applied offset: 0.0000 m")
            self.status_var.set(
                f"Re-baselined at {self.base_eye_height:.4f} m."
            )
        except Exception as e:
            messagebox.showerror("Re-baseline failed", str(e))

    def reset_offsets(self):
        if not self.running:
            self.status_var.set("Not running. Nothing to reset.")
            return

        try:
            self.vr.reset_offsets_added_by_app()

            if self.last_eye_height is not None:
                self.base_eye_height = self.last_eye_height
                self.need_baseline = False
                self.base_var.set(f"Baseline eye height: {self.base_eye_height:.4f} m")
            else:
                self.base_eye_height = None
                self.need_baseline = True

            self.offset_var.set("Applied offset: 0.0000 m")
            self.status_var.set("Reset offsets added by this app.")
        except Exception as e:
            messagebox.showerror("Reset failed", str(e))

    def on_close(self):
        try:
            if self.running:
                self.stop()
            else:
                self.vr.shutdown()
        except Exception:
            pass

        self.root.destroy()


def main():
    root = tk.Tk()
    app = EyeHeightLockApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
