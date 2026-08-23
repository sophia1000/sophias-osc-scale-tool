import glob
import json
import math
import os
import queue
import re
import socket
import threading
import time
from dataclasses import asdict, dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, Optional
from urllib.parse import quote, unquote, urlparse

import customtkinter as ctk
import requests
from pythonosc import dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer
from pythonosc.udp_client import SimpleUDPClient
from zeroconf import ServiceBrowser, ServiceInfo, ServiceListener, ServiceStateChange, Zeroconf


APP_NAME = "VRC Height OSC"
CONFIG_FILE = "vrc_height_osc_config.json"

OSCQUERY_TYPE = "_oscjson._tcp.local."
OSC_UDP_TYPE = "_osc._udp.local."

DEFAULT_VRCHAT_IP = "127.0.0.1"
DEFAULT_VRCHAT_OSC_PORT = 9000

PATH_EYE_HEIGHT = "/avatar/eyeheight"
PATH_EYE_MIN = "/avatar/eyeheightmin"
PATH_EYE_MAX = "/avatar/eyeheightmax"
PATH_SCALING_ALLOWED = "/avatar/eyeheightscalingallowed"

MIN_OSC_HEIGHT = 0.01
MAX_OSC_HEIGHT = 10000.0

UI_EVENTS = queue.Queue()


def clamp(v, lo, hi):
    try:
        v = float(v)
        if math.isnan(v) or math.isinf(v):
            return lo
        return max(lo, min(hi, v))
    except Exception:
        return lo


def flatten_value(v):
    while isinstance(v, list) and len(v) == 1:
        v = v[0]
    return v


def safe_float(v, default=0.0):
    v = flatten_value(v)
    try:
        return float(v)
    except Exception:
        return default


def boolish(v):
    v = flatten_value(v)
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return float(v) > 0.5
    if isinstance(v, str):
        return v.strip().lower() in ("true", "1", "yes", "on", "t")
    return False


def first_arg(args):
    if not args:
        return None
    if len(args) == 1:
        return args[0]
    return list(args)


def get_lan_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(0.2)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


def normalize_param_name(name):
    s = str(name or "").strip()
    if s.startswith("/avatar/parameters/"):
        s = s.split("/avatar/parameters/", 1)[1]
    return unquote(s).strip()


def param_name_from_address(address):
    return normalize_param_name(address)


def oscquery_url(base, path, query=""):
    url = f"{base}{quote(path, safe='/')}"
    if query:
        url += query
    return url


@dataclass
class Rule:
    enabled: bool = True
    parameter: str = ""
    height_value: float = 1.6

    mode: str = "trigger"          # trigger / follow
    condition: str = "true"        # true / false / above / below
    threshold: float = 0.5
    action: str = "set"            # set / add
    cooldown: float = 1.0
    rising_edge_only: bool = True

    smooth_enabled: bool = False
    smooth_time: float = 0.35

    limit_enabled: bool = False
    limit_min: float = 0.5
    limit_max: float = 2.0
    limit_behavior: str = "clamp"  # clamp / block_outside / toward_range

    follow_input_min: float = 0.0
    follow_input_max: float = 1.0
    follow_height_min: float = 0.5
    follow_height_max: float = 2.0
    follow_deadband: float = 0.005

    last_fire: float = 0.0
    was_active: bool = False
    last_follow_value: Optional[float] = None
    last_follow_height: Optional[float] = None


class AppState:
    def __init__(self):
        self.lock = threading.RLock()

        self.eyeheight = None
        self.eyeheightmin = None
        self.eyeheightmax = None
        self.scalingallowed = None

        self.avatar_id = ""
        self.params = {}

        self.vrchat_query_ip = None
        self.vrchat_query_port = None
        self.vrchat_service_name = None

        self.vrchat_osc_ip = DEFAULT_VRCHAT_IP
        self.vrchat_osc_port = DEFAULT_VRCHAT_OSC_PORT

        self.vrchat_found = False
        self.last_vrchat_seen = 0.0
        self.query_fail_count = 0

        self.local_ip = get_lan_ip()
        self.local_osc_port = None
        self.local_query_port = None

        self.network_generation = 0
        self.hard_restart_count = 0

        self.last_status = "Starting..."

        self.rules = []
        self.ui_config = {}

    def set_status(self, text):
        with self.lock:
            self.last_status = str(text)

    def update_value(self, path, value):
        path = unquote(str(path))
        value = flatten_value(value)

        with self.lock:
            if path == PATH_EYE_HEIGHT:
                self.eyeheight = safe_float(value, self.eyeheight or 1.6)
            elif path == PATH_EYE_MIN:
                self.eyeheightmin = safe_float(value, self.eyeheightmin or 0.0)
            elif path == PATH_EYE_MAX:
                self.eyeheightmax = safe_float(value, self.eyeheightmax or 0.0)
            elif path == PATH_SCALING_ALLOWED:
                self.scalingallowed = boolish(value)
            elif path == "/avatar/change":
                self.avatar_id = str(value)
            elif path.startswith("/avatar/parameters/"):
                self.params[param_name_from_address(path)] = value

    def clear_remote_connection(self):
        with self.lock:
            self.vrchat_query_ip = None
            self.vrchat_query_port = None
            self.vrchat_service_name = None
            self.vrchat_found = False
            self.last_vrchat_seen = 0.0
            self.query_fail_count = 0
            self.vrchat_osc_ip = DEFAULT_VRCHAT_IP
            self.vrchat_osc_port = DEFAULT_VRCHAT_OSC_PORT

    def snapshot(self):
        with self.lock:
            return {
                "eyeheight": self.eyeheight,
                "eyeheightmin": self.eyeheightmin,
                "eyeheightmax": self.eyeheightmax,
                "scalingallowed": self.scalingallowed,
                "avatar_id": self.avatar_id,
                "params": dict(self.params),

                "vrchat_query_ip": self.vrchat_query_ip,
                "vrchat_query_port": self.vrchat_query_port,
                "vrchat_service_name": self.vrchat_service_name,
                "vrchat_osc_ip": self.vrchat_osc_ip,
                "vrchat_osc_port": self.vrchat_osc_port,
                "vrchat_found": self.vrchat_found,
                "last_vrchat_seen": self.last_vrchat_seen,
                "query_fail_count": self.query_fail_count,

                "local_ip": self.local_ip,
                "local_osc_port": self.local_osc_port,
                "local_query_port": self.local_query_port,

                "network_generation": self.network_generation,
                "hard_restart_count": self.hard_restart_count,

                "last_status": self.last_status,
                "rules": list(self.rules),
                "ui_config": dict(self.ui_config),
            }


STATE = AppState()


class ConfigManager:
    def __init__(self):
        self.lock = threading.RLock()
        self.timer = None

    def load(self):
        if not os.path.exists(CONFIG_FILE):
            return

        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)

            fields = set(Rule.__dataclass_fields__.keys())
            rules = []

            for item in data.get("rules", []):
                clean = {k: v for k, v in item.items() if k in fields}
                r = Rule(**clean)
                r.parameter = normalize_param_name(r.parameter)
                r.last_fire = 0.0
                r.was_active = False
                r.last_follow_value = None
                r.last_follow_height = None
                rules.append(r)

            with STATE.lock:
                STATE.rules = rules
                STATE.ui_config = data.get("ui", {}) if isinstance(data.get("ui"), dict) else {}

            STATE.set_status("Config loaded.")

        except Exception as e:
            STATE.set_status(f"Config load failed: {e}")

    def schedule_save(self, delay=0.35):
        with self.lock:
            if self.timer:
                try:
                    self.timer.cancel()
                except Exception:
                    pass
            self.timer = threading.Timer(delay, self.save_now)
            self.timer.daemon = True
            self.timer.start()

    def save_now(self):
        with self.lock:
            self.timer = None

        try:
            with STATE.lock:
                rules = [asdict(r) for r in STATE.rules]
                ui = dict(STATE.ui_config)

            for r in rules:
                r["last_fire"] = 0.0
                r["was_active"] = False
                r["last_follow_value"] = None
                r["last_follow_height"] = None

            data = {
                "version": 3,
                "ui": ui,
                "rules": rules,
            }

            tmp = CONFIG_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)

            os.replace(tmp, CONFIG_FILE)

        except Exception as e:
            STATE.set_status(f"Config save failed: {e}")


CONFIG = ConfigManager()


def apply_rule_height_limits(rule, current_height, target_height):
    target_height = float(target_height)

    if not rule.limit_enabled:
        return clamp(target_height, MIN_OSC_HEIGHT, MAX_OSC_HEIGHT)

    lo = min(float(rule.limit_min), float(rule.limit_max))
    hi = max(float(rule.limit_min), float(rule.limit_max))

    if current_height is None:
        current_height = target_height

    current_height = float(current_height)

    if rule.limit_behavior == "clamp":
        return clamp(target_height, lo, hi)

    if lo <= current_height <= hi:
        return clamp(target_height, lo, hi)

    if current_height < lo:
        if rule.limit_behavior == "block_outside":
            return None
        if rule.limit_behavior == "toward_range":
            if target_height <= current_height:
                return None
            return clamp(target_height, lo, hi)

    if current_height > hi:
        if rule.limit_behavior == "block_outside":
            return None
        if rule.limit_behavior == "toward_range":
            if target_height >= current_height:
                return None
            return clamp(target_height, lo, hi)

    return clamp(target_height, lo, hi)


def node(full_path, description="", typ=None, value=None, contents=None, access=3):
    d = {
        "FULL_PATH": full_path,
        "ACCESS": access,
        "DESCRIPTION": description,
    }
    if typ:
        d["TYPE"] = typ
    if value is not None:
        d["VALUE"] = value
    if contents is not None:
        d["CONTENTS"] = contents
    return d


def build_local_oscquery_tree():
    snap = STATE.snapshot()

    avatar_contents = {
        "change": node("/avatar/change", "Avatar change event", "s", snap["avatar_id"], access=1),
        "eyeheight": node(PATH_EYE_HEIGHT, "Avatar eye height", "f", snap["eyeheight"], access=3),
        "eyeheightmin": node(PATH_EYE_MIN, "Udon min height", "f", snap["eyeheightmin"], access=1),
        "eyeheightmax": node(PATH_EYE_MAX, "Udon max height", "f", snap["eyeheightmax"], access=1),
        "eyeheightscalingallowed": node(
            PATH_SCALING_ALLOWED,
            "Scaling allowed",
            "T",
            snap["scalingallowed"],
            access=1,
        ),
        "parameters": node(
            "/avatar/parameters",
            "Avatar parameters from VRChat",
            contents={},
            access=1,
        ),
    }

    return node(
        "/",
        APP_NAME,
        contents={
            "avatar": node(
                "/avatar",
                "Advertises /avatar so VRChat sends avatar/parameters",
                contents=avatar_contents,
                access=1,
            )
        },
        access=1,
    )


def find_node(root, path):
    if path == "/":
        return root

    parts = [p for p in unquote(path).split("/") if p]
    cur = root

    for p in parts:
        contents = cur.get("CONTENTS", {})
        if not isinstance(contents, dict) or p not in contents:
            return None
        cur = contents[p]

    return cur


class OSCQueryHTTPHandler(BaseHTTPRequestHandler):
    server_version = "VRCHeightOSCQuery/4.0"

    def log_message(self, fmt, *args):
        return

    def _send_json(self, payload, code=200):
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlparse(self.path)
        path = unquote(parsed.path or "/")
        query = parsed.query or ""

        if "HOST_INFO" in query:
            snap = STATE.snapshot()
            self._send_json({
                "NAME": APP_NAME,
                "OSC_IP": "127.0.0.1",
                "OSC_PORT": int(snap["local_osc_port"] or 0),
                "OSC_TRANSPORT": "UDP",
                "EXTENSIONS": {
                    "ACCESS": True,
                    "VALUE": True,
                    "DESCRIPTION": True,
                    "TYPE": True,
                },
            })
            return

        tree = build_local_oscquery_tree()

        if path == "/":
            self._send_json(tree)
            return

        n = find_node(tree, path)
        if n is None:
            self._send_json({"ERROR": "Not found", "FULL_PATH": path}, code=404)
            return

        self._send_json(n)


class LocalOSCQueryService:
    """
    Keeps this app discoverable.

    Main fix:
    - Keep Zeroconf service alive.
    - Periodically call update_service() to re-announce.
    - This lets VRChat rediscover this app after OSC toggle/restart.
    """

    def __init__(self):
        self.lock = threading.RLock()
        self.zc = None
        self.http_server = None
        self.http_thread = None
        self.infos = []
        self.running = False
        self.announce_stop = threading.Event()
        self.announce_thread = None
        self.announce_interval = 2.0

    def start(self, generation):
        with self.lock:
            self.stop()
            self.announce_stop.clear()

            self.http_server = ThreadingHTTPServer(("0.0.0.0", 0), OSCQueryHTTPHandler)
            STATE.local_query_port = int(self.http_server.server_address[1])

            self.http_thread = threading.Thread(
                target=self.http_server.serve_forever,
                daemon=True,
                name="LocalOSCQueryHTTP",
            )
            self.http_thread.start()

            self.zc = Zeroconf()
            self.infos = []

            snap = STATE.snapshot()
            local_ip = snap["local_ip"]
            osc_port = int(snap["local_osc_port"] or 0)
            query_port = int(snap["local_query_port"] or 0)

            unique = f"{os.getpid()}-{generation}-{int(time.time())}"

            addresses = []
            try:
                if local_ip and local_ip != "127.0.0.1":
                    addresses.append(socket.inet_aton(local_ip))
            except Exception:
                pass

            try:
                addresses.append(socket.inet_aton("127.0.0.1"))
            except Exception:
                pass

            if not addresses:
                addresses = [socket.inet_aton("127.0.0.1")]

            props = {
                "name": APP_NAME,
                "app": APP_NAME,
                "generation": str(generation),
            }

            query_info = ServiceInfo(
                OSCQUERY_TYPE,
                f"{APP_NAME}-{unique}._oscjson._tcp.local.",
                addresses=addresses,
                port=query_port,
                properties={
                    **props,
                    "osc_port": str(osc_port),
                    "http_port": str(query_port),
                },
                server=f"vrc-height-osc-{unique}.local.",
            )

            osc_info = ServiceInfo(
                OSC_UDP_TYPE,
                f"{APP_NAME}-{unique}._osc._udp.local.",
                addresses=addresses,
                port=osc_port,
                properties={
                    **props,
                    "osc_port": str(osc_port),
                },
                server=f"vrc-height-osc-{unique}.local.",
            )

            self.zc.register_service(query_info, allow_name_change=True)
            self.zc.register_service(osc_info, allow_name_change=True)

            self.infos = [query_info, osc_info]
            self.running = True

            self.announce_thread = threading.Thread(
                target=self._announce_loop,
                daemon=True,
                name="OSCQueryAnnouncer",
            )
            self.announce_thread.start()

            STATE.set_status(
                f"Local OSCQuery advertised. OSC UDP 127.0.0.1:{osc_port}, HTTP {query_port}, gen {generation}"
            )

        threading.Thread(
            target=self._startup_announce_burst,
            daemon=True,
            name="OSCQueryStartupAnnounceBurst",
        ).start()

    def _startup_announce_burst(self):
        for _ in range(8):
            if self.announce_stop.is_set():
                return
            self.announce_once("startup burst")
            time.sleep(0.35)

    def _announce_loop(self):
        while not self.announce_stop.wait(self.announce_interval):
            self.announce_once("periodic")

    def announce_once(self, reason="manual"):
        with self.lock:
            if not self.running or self.zc is None:
                return
            zc = self.zc
            infos = list(self.infos)

        ok = 0
        for info in infos:
            try:
                zc.update_service(info)
                ok += 1
            except Exception:
                pass

        if ok:
            STATE.set_status(f"OSCQuery announce sent: {reason}")

    def stop(self):
        with self.lock:
            self.announce_stop.set()

            if self.zc is not None:
                for info in list(self.infos):
                    try:
                        self.zc.unregister_service(info)
                    except Exception:
                        pass
                try:
                    self.zc.close()
                except Exception:
                    pass

            self.zc = None
            self.infos = []

            if self.http_server is not None:
                try:
                    self.http_server.shutdown()
                except Exception:
                    pass
                try:
                    self.http_server.server_close()
                except Exception:
                    pass

            self.http_server = None
            self.http_thread = None
            self.running = False


class OSCManager:
    def __init__(self):
        self.lock = threading.RLock()
        self.server = None
        self.thread = None
        self.client = None
        self.client_target = None
        self.smooth_lock = threading.RLock()
        self.smooth_generation = 0

    def start(self):
        with self.lock:
            self.stop()

            disp = dispatcher.Dispatcher()
            disp.map(PATH_EYE_HEIGHT, self._handle)
            disp.map(PATH_EYE_MIN, self._handle)
            disp.map(PATH_EYE_MAX, self._handle)
            disp.map(PATH_SCALING_ALLOWED, self._handle)
            disp.map("/avatar/change", self._handle)
            disp.map("/avatar/parameters/*", self._handle_param)
            disp.set_default_handler(self._handle_default)

            self.server = ThreadingOSCUDPServer(("0.0.0.0", 0), disp)
            STATE.local_osc_port = int(self.server.server_address[1])

            self.thread = threading.Thread(
                target=self.server.serve_forever,
                daemon=True,
                name="LocalOSCUDP",
            )
            self.thread.start()

            self.client = None
            self.client_target = None
            STATE.set_status(f"Local OSC UDP started on {STATE.local_osc_port}")

    def stop(self):
        with self.lock:
            with self.smooth_lock:
                self.smooth_generation += 1

            if self.server is not None:
                try:
                    self.server.shutdown()
                except Exception:
                    pass
                try:
                    self.server.server_close()
                except Exception:
                    pass

            self.server = None
            self.thread = None
            self.client = None
            self.client_target = None

    def _get_client(self):
        snap = STATE.snapshot()
        target = (snap["vrchat_osc_ip"] or DEFAULT_VRCHAT_IP, int(snap["vrchat_osc_port"] or DEFAULT_VRCHAT_OSC_PORT))

        with self.lock:
            if self.client is None or self.client_target != target:
                self.client = SimpleUDPClient(target[0], target[1])
                self.client_target = target
            return self.client

    def _send_height_immediate(self, h, quiet=False):
        h = clamp(h, MIN_OSC_HEIGHT, MAX_OSC_HEIGHT)
        self._get_client().send_message(PATH_EYE_HEIGHT, float(h))
        STATE.update_value(PATH_EYE_HEIGHT, h)
        if not quiet:
            STATE.set_status(f"Sent height {h:.3f} m")

    def send_height(self, h, smooth=False, smooth_time=0.0):
        h = clamp(h, MIN_OSC_HEIGHT, MAX_OSC_HEIGHT)

        if not smooth or float(smooth_time) <= 0.01:
            with self.smooth_lock:
                self.smooth_generation += 1
            self._send_height_immediate(h)
            return

        with self.smooth_lock:
            self.smooth_generation += 1
            gen = self.smooth_generation

        threading.Thread(
            target=self._smooth_height_thread,
            args=(gen, h, float(smooth_time)),
            daemon=True,
            name="HeightSmooth",
        ).start()

    def _smooth_height_thread(self, gen, target, duration):
        snap = STATE.snapshot()
        start = snap["eyeheight"]
        if start is None:
            start = target

        start = float(start)
        target = clamp(target, MIN_OSC_HEIGHT, MAX_OSC_HEIGHT)
        duration = clamp(duration, 0.02, 10.0)

        steps = max(2, int(duration * 30.0))
        delay = duration / steps

        for i in range(1, steps + 1):
            with self.smooth_lock:
                if gen != self.smooth_generation:
                    return

            t = i / steps
            eased = t * t * (3.0 - 2.0 * t)
            h = start + (target - start) * eased
            self._send_height_immediate(h, quiet=True)
            time.sleep(delay)

        self._send_height_immediate(target)

    def _handle(self, address, *args):
        address = unquote(str(address))
        val = first_arg(args)
        STATE.update_value(address, val)
        UI_EVENTS.put(("osc", address, val))

    def _handle_param(self, address, *args):
        address = unquote(str(address))
        val = first_arg(args)
        STATE.update_value(address, val)
        self._evaluate_rules(address, val)
        UI_EVENTS.put(("param", address, val))

    def _handle_default(self, address, *args):
        address = unquote(str(address))
        val = first_arg(args)
        STATE.update_value(address, val)

        if address.startswith("/avatar/parameters/"):
            self._evaluate_rules(address, val)

        UI_EVENTS.put(("osc", address, val))

    def trigger_rule_action(self, rule, ignore_cooldown=True):
        now = time.time()

        if not ignore_cooldown and now - rule.last_fire < max(0.0, rule.cooldown):
            return

        snap = STATE.snapshot()
        current = snap["eyeheight"] if snap["eyeheight"] is not None else 1.6

        if rule.mode == "follow":
            pname = normalize_param_name(rule.parameter)
            value = safe_float(snap["params"].get(pname, rule.follow_input_min), rule.follow_input_min)
            raw = self._map_follow_height(rule, value)
        else:
            raw = rule.height_value if rule.action == "set" else float(current) + rule.height_value

        target = apply_rule_height_limits(rule, current, raw)

        if target is None:
            STATE.set_status(f"Rule '{rule.parameter}' blocked by height limit.")
            return

        rule.last_fire = now
        self.send_height(target, rule.smooth_enabled, rule.smooth_time)

    def _map_follow_height(self, rule, value):
        in_min = float(rule.follow_input_min)
        in_max = float(rule.follow_input_max)

        if abs(in_max - in_min) < 0.000001:
            t = 0.0
        else:
            t = (float(value) - in_min) / (in_max - in_min)

        t = clamp(t, 0.0, 1.0)
        return float(rule.follow_height_min) + (float(rule.follow_height_max) - float(rule.follow_height_min)) * t

    def _evaluate_rules(self, address, value):
        if not address.startswith("/avatar/parameters/"):
            return

        pname = param_name_from_address(address)

        with STATE.lock:
            rules = list(STATE.rules)

        now = time.time()

        for rule in rules:
            if not rule.enabled:
                continue
            if normalize_param_name(rule.parameter) != pname:
                continue

            numeric = safe_float(value, 1.0 if boolish(value) else 0.0)

            if rule.mode == "follow":
                self._evaluate_follow_rule(rule, numeric)
                continue

            if rule.condition == "true":
                active = boolish(value)
            elif rule.condition == "false":
                active = not boolish(value)
            elif rule.condition == "above":
                active = numeric > rule.threshold
            elif rule.condition == "below":
                active = numeric < rule.threshold
            else:
                active = False

            should_fire = active
            if rule.rising_edge_only:
                should_fire = active and not rule.was_active
            if now - rule.last_fire < max(0.0, rule.cooldown):
                should_fire = False

            rule.was_active = active

            if should_fire:
                self.trigger_rule_action(rule, ignore_cooldown=False)

    def _evaluate_follow_rule(self, rule, value):
        deadband = max(0.0, float(rule.follow_deadband))

        if rule.last_follow_value is not None:
            if abs(float(value) - float(rule.last_follow_value)) < deadband:
                return

        raw = self._map_follow_height(rule, value)
        snap = STATE.snapshot()
        current = snap["eyeheight"] if snap["eyeheight"] is not None else 1.6

        target = apply_rule_height_limits(rule, current, raw)

        rule.last_follow_value = float(value)

        if target is None:
            STATE.set_status(f"Follow rule '{rule.parameter}' blocked by height limit.")
            return

        if rule.last_follow_height is not None:
            if abs(float(target) - float(rule.last_follow_height)) < 0.0005:
                return

        rule.last_follow_height = float(target)
        rule.last_fire = time.time()
        self.send_height(target, rule.smooth_enabled, rule.smooth_time)


OSC = OSCManager()


class OSCQueryScanListener(ServiceListener):
    """
    Correct Zeroconf ServiceListener.

    This avoids the old callback error:
    unexpected keyword argument 'zeroconf'
    """

    def __init__(self):
        self.lock = threading.RLock()
        self.services = {}

    def add_service(self, zeroconf: Zeroconf, type_: str, name: str):
        self._record_service(zeroconf, type_, name, ServiceStateChange.Added)

    def update_service(self, zeroconf: Zeroconf, type_: str, name: str):
        self._record_service(zeroconf, type_, name, ServiceStateChange.Updated)

    def remove_service(self, zeroconf: Zeroconf, type_: str, name: str):
        # A restarted VRChat instance normally comes back with a new service
        # name and port.  Do not leave the old endpoint in the candidate list.
        with self.lock:
            self.services.pop(name, None)

    def _record_service(self, zeroconf, type_, name, state_change):
        if APP_NAME.lower() in name.lower():
            return

        try:
            info = zeroconf.get_service_info(type_, name, timeout=1000)
        except Exception:
            info = None

        if not info:
            return

        try:
            addresses = info.parsed_addresses()
        except Exception:
            addresses = []

        if not addresses:
            addresses = ["127.0.0.1"]

        props = {}
        try:
            for k, v in dict(info.properties or {}).items():
                kk = k.decode("utf-8", "ignore") if isinstance(k, bytes) else str(k)
                vv = v.decode("utf-8", "ignore") if isinstance(v, bytes) else str(v)
                props[kk] = vv
        except Exception:
            pass

        with self.lock:
            self.services[name] = {
                "name": name,
                "port": int(info.port),
                "addresses": list(addresses),
                "properties": props,
                "time": time.time(),
            }

    def snapshot(self):
        with self.lock:
            return [dict(item) for item in self.services.values()]


class PersistentVRChatBrowser:
    """
    Persistent browser.

    The old version created/destroyed the browser repeatedly.
    This stays alive and listens continuously for VRChat.
    """

    def __init__(self):
        self.lock = threading.RLock()
        self.zc = None
        self.browser = None
        self.listener = None
        self.running = False

    def start(self):
        with self.lock:
            if self.running and self.zc and self.listener:
                return

            self.stop()

            self.listener = OSCQueryScanListener()
            self.zc = Zeroconf()

            try:
                self.browser = ServiceBrowser(self.zc, OSCQUERY_TYPE, listener=self.listener)
            except TypeError:
                self.browser = ServiceBrowser(self.zc, OSCQUERY_TYPE, self.listener)

            self.running = True
            STATE.set_status("Persistent VRChat OSCQuery browser started.")

    def stop(self):
        with self.lock:
            try:
                if self.browser:
                    self.browser.cancel()
            except Exception:
                pass
            try:
                if self.zc:
                    self.zc.close()
            except Exception:
                pass

            self.browser = None
            self.zc = None
            self.listener = None
            self.running = False

    def restart(self):
        """Force a fresh mDNS query instead of trusting a stale browser."""
        with self.lock:
            self.stop()
            self.start()

    def services(self):
        with self.lock:
            if not self.listener:
                return []
            return self.listener.snapshot()


class NetworkSupervisor:
    def __init__(self):
        self.local_query = LocalOSCQueryService()
        self.discovery = PersistentVRChatBrowser()

        self.stop_event = threading.Event()
        self.wake_event = threading.Event()
        self.command_lock = threading.RLock()
        self.pending_hard_restart_reason = None
        self.thread = None

        self.bad_endpoints = {}
        self.last_search_restart = 0.0
        self.last_discovery_restart = 0.0
        self.heartbeat_failures = 0

    def start(self):
        self.thread = threading.Thread(target=self._run, daemon=True, name="NetworkSupervisor")
        self.thread.start()

    def stop(self):
        self.stop_event.set()
        self.wake_event.set()

        try:
            self.discovery.stop()
        except Exception:
            pass
        try:
            self.local_query.stop()
        except Exception:
            pass
        try:
            OSC.stop()
        except Exception:
            pass

    def request_hard_restart(self, reason):
        with self.command_lock:
            self.pending_hard_restart_reason = str(reason)
        self.wake_event.set()

    def _pop_hard_restart_reason(self):
        with self.command_lock:
            r = self.pending_hard_restart_reason
            self.pending_hard_restart_reason = None
            return r

    def _run(self):
        self.discovery.start()
        self._hard_restart("initial start")

        last_refresh = 0.0
        last_scan = 0.0
        last_announce = 0.0
        self.last_search_restart = time.time()
        self.last_discovery_restart = time.time()

        while not self.stop_event.is_set():
            reason = self._pop_hard_restart_reason()
            if reason:
                self._hard_restart(reason)
                last_refresh = 0.0
                last_scan = 0.0
                last_announce = 0.0
                self.last_search_restart = time.time()

            self.discovery.start()

            # Important fix: periodic announce even while connected/searching.
            if time.time() - last_announce > 2.0:
                self.local_query.announce_once("supervisor periodic")
                last_announce = time.time()

            snap = STATE.snapshot()

            if snap["vrchat_found"]:
                if not self._heartbeat():
                    self.heartbeat_failures += 1
                    if self.heartbeat_failures < 3:
                        STATE.set_status(
                            f"VRChat heartbeat missed ({self.heartbeat_failures}/3); retrying."
                        )
                        self.wake_event.wait(0.5)
                        self.wake_event.clear()
                        continue

                    ep = (snap["vrchat_query_ip"], int(snap["vrchat_query_port"] or 0))
                    if ep[0] and ep[1]:
                        self.bad_endpoints[ep] = time.time() + 5.0

                    # Keep this app's receiving ports stable.  VRChat already
                    # knows them; only its OSCQuery endpoint/browser is stale.
                    STATE.clear_remote_connection()
                    self._restart_discovery("heartbeat lost")
                    self.local_query.announce_once("VRChat heartbeat lost")
                    self.heartbeat_failures = 0
                    last_refresh = 0.0
                    last_scan = 0.0
                    last_announce = 0.0
                    self.last_search_restart = time.time()
                    continue

                self.heartbeat_failures = 0
                if time.time() - last_refresh > 2.0:
                    self.refresh_live_values_now()
                    last_refresh = time.time()

                self.wake_event.wait(1.0)
                self.wake_event.clear()
                continue

            # Not connected: keep advertising and scan.
            # A Zeroconf browser can remain alive yet stop producing useful
            # events after an interface/game restart. Recreate it periodically
            # so a fresh mDNS query is sent for VRChat's new endpoint.
            if time.time() - self.last_discovery_restart > 10.0:
                self._restart_discovery("still searching")

            if time.time() - last_scan > 1.0:
                if self._scan_and_connect_once():
                    last_refresh = 0.0
                    last_scan = time.time()
                    threading.Thread(
                        target=self._post_connect_announce_burst,
                        daemon=True,
                        name="PostConnectAnnounceBurst",
                    ).start()
                    continue
                last_scan = time.time()

            # Do not rebuild too often. Constantly changing ports can make discovery worse.
            if time.time() - self.last_search_restart > 60.0:
                self._hard_restart("still searching after 60s")
                self.last_search_restart = time.time()
                last_scan = 0.0
                last_announce = 0.0
                continue

            self.wake_event.wait(1.0)
            self.wake_event.clear()

    def _post_connect_announce_burst(self):
        for _ in range(8):
            if self.stop_event.is_set():
                return
            self.local_query.announce_once("post-connect burst")
            time.sleep(0.4)

    def _restart_discovery(self, reason):
        try:
            self.discovery.restart()
            self.last_discovery_restart = time.time()
            STATE.set_status(f"OSCQuery discovery restarted: {reason}")
        except Exception as e:
            # Leave the supervisor alive so the next loop can retry.
            self.last_discovery_restart = time.time()
            STATE.set_status(f"OSCQuery discovery restart failed: {e}")

    def _hard_restart(self, reason):
        STATE.set_status(f"Hard reconnect: rebuilding OSC/OSCQuery. Reason: {reason}")

        with STATE.lock:
            STATE.hard_restart_count += 1
            STATE.network_generation += 1
            gen = STATE.network_generation

        STATE.clear_remote_connection()

        self._restart_discovery(reason)

        try:
            self.local_query.stop()
        except Exception:
            pass

        try:
            OSC.stop()
        except Exception:
            pass

        time.sleep(0.35)

        STATE.local_ip = get_lan_ip()

        try:
            OSC.start()
            self.local_query.start(gen)
        except Exception as e:
            STATE.set_status(f"Hard reconnect failed: {e}")
            return

        for _ in range(5):
            self.local_query.announce_once(f"hard reconnect {reason}")
            time.sleep(0.2)

        STATE.set_status(f"Hard reconnect complete. Fresh advertisement active. Reason: {reason}")

    def _heartbeat(self):
        snap = STATE.snapshot()
        ip = snap["vrchat_query_ip"]
        port = snap["vrchat_query_port"]

        if not ip or not port:
            return False

        base = f"http://{ip}:{port}"

        try:
            r = requests.get(f"{base}/?HOST_INFO", timeout=0.7, headers={"Connection": "close"})
            if not r.ok:
                return False

            host = r.json()

            osc_ip = host.get("OSC_IP") or snap["vrchat_osc_ip"] or DEFAULT_VRCHAT_IP
            osc_port = host.get("OSC_PORT") or snap["vrchat_osc_port"] or DEFAULT_VRCHAT_OSC_PORT

            if osc_ip in ("0.0.0.0", "::", "", None):
                osc_ip = DEFAULT_VRCHAT_IP

            with STATE.lock:
                STATE.vrchat_found = True
                STATE.last_vrchat_seen = time.time()
                STATE.query_fail_count = 0
                STATE.vrchat_osc_ip = str(osc_ip)
                STATE.vrchat_osc_port = int(osc_port)

            return True

        except Exception:
            with STATE.lock:
                STATE.query_fail_count += 1
            return False

    def _scan_and_connect_once(self):
        STATE.set_status("Scanning for VRChat OSCQuery endpoint...")
        self._expire_bad_endpoints()
        self.local_query.announce_once("scan")

        # Fast fallback: read VRChat log for current OSCQuery port.
        for port in self._find_vrchat_oscquery_ports_from_logs():
            if self._endpoint_is_bad("127.0.0.1", port):
                continue
            if self._try_connect_candidate("127.0.0.1", port, f"VRChat-log-port-{port}"):
                return True

        services = self.discovery.services()
        services.sort(key=lambda s: 0 if "vrchat" in s["name"].lower() else 1)

        for svc in services:
            name = svc["name"]
            port = int(svc["port"])
            addresses = list(svc["addresses"])

            candidates = ["127.0.0.1", "localhost"]
            for a in addresses:
                if a not in candidates:
                    candidates.append(a)

            for host in candidates:
                if self._endpoint_is_bad(host, port):
                    continue
                if self._try_connect_candidate(host, port, name):
                    return True

        return False

    def _endpoint_is_bad(self, ip, port):
        return self.bad_endpoints.get((str(ip), int(port)), 0) > time.time()

    def _expire_bad_endpoints(self):
        now = time.time()
        for ep, expiry in list(self.bad_endpoints.items()):
            if expiry <= now:
                self.bad_endpoints.pop(ep, None)

    def _try_connect_candidate(self, ip, port, service_name=""):
        base = f"http://{ip}:{port}"

        try:
            host_r = requests.get(f"{base}/?HOST_INFO", timeout=0.8, headers={"Connection": "close"})
            if not host_r.ok:
                self.bad_endpoints[(str(ip), int(port))] = time.time() + 5.0
                return False

            host_info = host_r.json()
            reported_name = str(host_info.get("NAME", service_name))

            root_paths = set()
            for suffix in ("", "?VALUE"):
                try:
                    root_r = requests.get(f"{base}/{suffix}", timeout=1.0, headers={"Connection": "close"})
                    if root_r.ok:
                        root_paths = self._collect_paths(root_r.json())
                        break
                except Exception:
                    pass

            looks_like_vrchat = (
                "vrchat" in reported_name.lower()
                or "vrchat" in str(service_name).lower()
                or "/chatbox/input" in root_paths
                or "/input/Vertical" in root_paths
                or PATH_EYE_HEIGHT in root_paths
                or PATH_SCALING_ALLOWED in root_paths
            )

            if not looks_like_vrchat:
                return False

            osc_ip = host_info.get("OSC_IP") or DEFAULT_VRCHAT_IP
            osc_port = host_info.get("OSC_PORT") or DEFAULT_VRCHAT_OSC_PORT

            if osc_ip in ("0.0.0.0", "::", "", None):
                osc_ip = DEFAULT_VRCHAT_IP

            with STATE.lock:
                STATE.vrchat_query_ip = str(ip)
                STATE.vrchat_query_port = int(port)
                STATE.vrchat_service_name = str(service_name)
                STATE.vrchat_osc_ip = str(osc_ip)
                STATE.vrchat_osc_port = int(osc_port)
                STATE.vrchat_found = True
                STATE.last_vrchat_seen = time.time()
                STATE.query_fail_count = 0

            STATE.set_status(
                f"Connected to VRChat OSCQuery {ip}:{port}; OSC target {osc_ip}:{osc_port}"
            )

            self.refresh_live_values_now()
            self.local_query.announce_once("connected to VRChat")
            return True

        except Exception:
            self.bad_endpoints[(str(ip), int(port))] = time.time() + 5.0
            return False

    def _collect_paths(self, data):
        paths = set()

        def walk(n):
            if not isinstance(n, dict):
                return
            fp = n.get("FULL_PATH")
            if isinstance(fp, str):
                paths.add(unquote(fp))
            contents = n.get("CONTENTS", {})
            if isinstance(contents, dict):
                for child in contents.values():
                    walk(child)

        walk(data)
        return paths

    def refresh_live_values_now(self):
        snap = STATE.snapshot()

        if not snap["vrchat_query_ip"] or not snap["vrchat_query_port"]:
            return False

        base = f"http://{snap['vrchat_query_ip']}:{snap['vrchat_query_port']}"
        fetched = False

        def apply_node(data):
            nonlocal fetched

            if not isinstance(data, dict):
                return

            fp = data.get("FULL_PATH")
            if isinstance(fp, str):
                fp = unquote(fp)

                if "VALUE" in data:
                    val = flatten_value(data["VALUE"])

                    if (
                        fp in [
                            PATH_EYE_HEIGHT,
                            PATH_EYE_MIN,
                            PATH_EYE_MAX,
                            PATH_SCALING_ALLOWED,
                            "/avatar/change",
                        ]
                        or fp.startswith("/avatar/parameters/")
                    ):
                        STATE.update_value(fp, val)
                        fetched = True

            contents = data.get("CONTENTS", {})
            if isinstance(contents, dict):
                for child in contents.values():
                    apply_node(child)

        direct = [
            PATH_EYE_HEIGHT,
            PATH_EYE_MIN,
            PATH_EYE_MAX,
            PATH_SCALING_ALLOWED,
            "/avatar/change",
            "/avatar/parameters",
        ]

        for path in direct:
            for suffix in ("?VALUE", ""):
                try:
                    r = requests.get(
                        oscquery_url(base, path, suffix),
                        timeout=0.9,
                        headers={"Connection": "close"},
                    )
                    if r.ok:
                        apply_node(r.json())
                except Exception:
                    pass

        for suffix in ("?VALUE", ""):
            try:
                r = requests.get(f"{base}/{suffix}", timeout=1.2, headers={"Connection": "close"})
                if r.ok:
                    apply_node(r.json())
            except Exception:
                pass

        if fetched:
            with STATE.lock:
                STATE.last_vrchat_seen = time.time()
                STATE.query_fail_count = 0
                STATE.vrchat_found = True

        return fetched

    def _find_vrchat_oscquery_ports_from_logs(self):
        ports = []
        candidates = []

        local_low = os.path.join(
            os.environ.get("USERPROFILE", ""),
            "AppData",
            "LocalLow",
            "VRChat",
            "VRChat",
        )

        if local_low and os.path.isdir(local_low):
            candidates.extend(glob.glob(os.path.join(local_low, "output_log*.txt")))
            candidates.extend(glob.glob(os.path.join(local_low, "output_log*.log")))

        candidates.extend(glob.glob("output_log*.txt"))
        candidates.extend(glob.glob("output_log*.log"))

        candidates = sorted(
            set(candidates),
            key=lambda p: os.path.getmtime(p) if os.path.exists(p) else 0,
            reverse=True,
        )[:5]

        patterns = [
            re.compile(r"Advertising Service .*?VRChat.*? type OSCQuery on (\d+)", re.IGNORECASE),
            re.compile(r"Advertising Service .*?OSCQuery.*? on (\d+)", re.IGNORECASE),
            re.compile(r"_oscjson\._tcp.*?(\d{4,5})", re.IGNORECASE),
            re.compile(r"OSCQuery.*?port[: ]+(\d{4,5})", re.IGNORECASE),
        ]

        for file_path in candidates:
            try:
                with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                    data = f.read()[-700000:]

                found = []
                for line in data.splitlines():
                    low = line.lower()
                    if "oscquery" not in low and "_oscjson" not in low:
                        continue

                    for pat in patterns:
                        m = pat.search(line)
                        if m:
                            p = int(m.group(1))
                            if 1024 <= p <= 65535:
                                found.append(p)

                for p in reversed(found):
                    if p not in ports:
                        ports.append(p)

            except Exception:
                pass

        return ports[:10]


NETWORK = NetworkSupervisor()


class HeightApp(ctk.CTk):
    def __init__(self):
        super().__init__()

        ctk.set_appearance_mode("dark")
        ctk.set_default_color_theme("blue")

        ui = STATE.snapshot()["ui_config"]

        self.title("VRC Height OSC")
        self.geometry(ui.get("geometry", "1160x800"))
        self.minsize(1060, 720)

        self.rule_widgets = []
        self.expanded_rules = set()

        self.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(1, weight=1)

        self.header = ctk.CTkFrame(self, corner_radius=18)
        self.header.grid(row=0, column=0, padx=16, pady=(16, 8), sticky="ew")
        self.header.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(
            self.header,
            text="VRChat Avatar Height Controller",
            font=ctk.CTkFont(size=26, weight="bold"),
        ).grid(row=0, column=0, padx=18, pady=(14, 0), sticky="w")

        self.status_label = ctk.CTkLabel(
            self.header,
            text="Starting...",
            text_color="#a9b8d8",
            font=ctk.CTkFont(size=13),
        )
        self.status_label.grid(row=1, column=0, padx=18, pady=(0, 14), sticky="w")

        ctk.CTkButton(
            self.header,
            text="Hard Reconnect",
            width=135,
            command=lambda: NETWORK.request_hard_restart("manual button"),
        ).grid(row=0, column=1, rowspan=2, padx=(8, 4), pady=14, sticky="e")

        ctk.CTkButton(
            self.header,
            text="Refresh Values",
            width=130,
            command=self.manual_refresh_values,
        ).grid(row=0, column=2, rowspan=2, padx=(4, 18), pady=14, sticky="e")

        self.main = ctk.CTkFrame(self, fg_color="transparent")
        self.main.grid(row=1, column=0, padx=16, pady=8, sticky="nsew")
        self.main.grid_columnconfigure(0, weight=1)
        self.main.grid_columnconfigure(1, weight=1)
        self.main.grid_rowconfigure(1, weight=1)

        self.values_frame = ctk.CTkFrame(self.main, corner_radius=18)
        self.values_frame.grid(row=0, column=0, padx=(0, 8), pady=(0, 8), sticky="nsew")
        self.values_frame.grid_columnconfigure((0, 1), weight=1)

        ctk.CTkLabel(
            self.values_frame,
            text="Live Values",
            font=ctk.CTkFont(size=20, weight="bold"),
        ).grid(row=0, column=0, columnspan=2, padx=18, pady=(16, 8), sticky="w")

        self.eye_label = self.value_card(self.values_frame, 1, 0, "Eye Height", "-- m")
        self.min_label = self.value_card(self.values_frame, 1, 1, "Udon Min", "-- m")
        self.max_label = self.value_card(self.values_frame, 2, 0, "Udon Max", "-- m")
        self.allowed_label = self.value_card(self.values_frame, 2, 1, "Scaling Allowed", "--")

        self.control_frame = ctk.CTkFrame(self.main, corner_radius=18)
        self.control_frame.grid(row=0, column=1, padx=(8, 0), pady=(0, 8), sticky="nsew")
        self.control_frame.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(
            self.control_frame,
            text="Set Eye Height",
            font=ctk.CTkFont(size=20, weight="bold"),
        ).grid(row=0, column=0, padx=18, pady=(16, 8), sticky="w")

        initial_height = safe_float(ui.get("height_value", 1.6), 1.6)
        self.height_var = ctk.DoubleVar(value=initial_height)

        self.height_slider = ctk.CTkSlider(
            self.control_frame,
            from_=0.1,
            to=5.0,
            variable=self.height_var,
            command=self.on_slider,
        )
        self.height_slider.grid(row=1, column=0, padx=18, pady=(10, 4), sticky="ew")

        self.height_entry = ctk.CTkEntry(self.control_frame, placeholder_text="Height in meters")
        self.height_entry.insert(0, f"{initial_height:.2f}")
        self.height_entry.grid(row=2, column=0, padx=18, pady=8, sticky="ew")
        self.height_entry.bind("<KeyRelease>", lambda e: self.save_ui_settings_soon())

        btn_row = ctk.CTkFrame(self.control_frame, fg_color="transparent")
        btn_row.grid(row=3, column=0, padx=18, pady=(8, 4), sticky="ew")
        btn_row.grid_columnconfigure((0, 1, 2), weight=1)

        ctk.CTkButton(btn_row, text="Set", command=self.set_height_from_entry).grid(row=0, column=0, padx=4, sticky="ew")
        ctk.CTkButton(btn_row, text="-0.10", command=lambda: self.add_height(-0.10)).grid(row=0, column=1, padx=4, sticky="ew")
        ctk.CTkButton(btn_row, text="+0.10", command=lambda: self.add_height(0.10)).grid(row=0, column=2, padx=4, sticky="ew")

        btn_row2 = ctk.CTkFrame(self.control_frame, fg_color="transparent")
        btn_row2.grid(row=4, column=0, padx=18, pady=(4, 8), sticky="ew")
        btn_row2.grid_columnconfigure((0, 1, 2, 3), weight=1)

        ctk.CTkButton(btn_row2, text="-0.05", command=lambda: self.add_height(-0.05)).grid(row=0, column=0, padx=4, sticky="ew")
        ctk.CTkButton(btn_row2, text="+0.05", command=lambda: self.add_height(0.05)).grid(row=0, column=1, padx=4, sticky="ew")
        ctk.CTkButton(btn_row2, text="-0.01", command=lambda: self.add_height(-0.01)).grid(row=0, column=2, padx=4, sticky="ew")
        ctk.CTkButton(btn_row2, text="+0.01", command=lambda: self.add_height(0.01)).grid(row=0, column=3, padx=4, sticky="ew")

        smooth_frame = ctk.CTkFrame(self.control_frame, fg_color="#151d2e", corner_radius=14)
        smooth_frame.grid(row=5, column=0, padx=18, pady=(6, 16), sticky="ew")
        smooth_frame.grid_columnconfigure(1, weight=1)

        self.main_smooth_enabled = ctk.BooleanVar(value=bool(ui.get("smooth_enabled", False)))
        self.main_smooth_time = ctk.StringVar(value=str(ui.get("smooth_time", "0.35")))

        self.main_smooth_enabled.trace_add("write", lambda *_: self.save_ui_settings_soon())
        self.main_smooth_time.trace_add("write", lambda *_: self.save_ui_settings_soon())

        ctk.CTkCheckBox(
            smooth_frame,
            text="Smooth main height changes",
            variable=self.main_smooth_enabled,
        ).grid(row=0, column=0, columnspan=2, padx=12, pady=(10, 6), sticky="w")

        ctk.CTkLabel(smooth_frame, text="Smooth time:").grid(row=1, column=0, padx=12, pady=(0, 10), sticky="w")
        ctk.CTkEntry(smooth_frame, textvariable=self.main_smooth_time, width=90).grid(row=1, column=1, padx=12, pady=(0, 10), sticky="e")

        self.rules_frame = ctk.CTkFrame(self.main, corner_radius=18)
        self.rules_frame.grid(row=1, column=0, columnspan=2, padx=0, pady=(8, 0), sticky="nsew")
        self.rules_frame.grid_columnconfigure(0, weight=1)
        self.rules_frame.grid_rowconfigure(2, weight=1)

        top = ctk.CTkFrame(self.rules_frame, fg_color="transparent")
        top.grid(row=0, column=0, padx=18, pady=(16, 8), sticky="ew")
        top.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(
            top,
            text="Custom Parameter Triggers",
            font=ctk.CTkFont(size=20, weight="bold"),
        ).grid(row=0, column=0, sticky="w")

        ctk.CTkButton(top, text="+ Add Rule", width=120, command=self.add_rule).grid(row=0, column=1, padx=6)
        ctk.CTkButton(top, text="Save Now", width=120, command=CONFIG.save_now).grid(row=0, column=2, padx=6)

        ctk.CTkLabel(
            self.rules_frame,
            text="Settings auto-save. Parameter names with spaces are supported. Reconnect now periodically announces to VRChat.",
            text_color="#9db0d0",
        ).grid(row=1, column=0, padx=18, pady=(0, 8), sticky="w")

        self.scroll = ctk.CTkScrollableFrame(self.rules_frame, corner_radius=14)
        self.scroll.grid(row=2, column=0, padx=18, pady=(0, 18), sticky="nsew")
        self.scroll.grid_columnconfigure(0, weight=1)

        self.refresh_rules_ui()

        self.after(250, self.update_loop)
        self.after(2000, self.save_geometry_loop)

    def value_card(self, parent, row, col, title, value):
        card = ctk.CTkFrame(parent, corner_radius=16, fg_color="#182034")
        card.grid(row=row, column=col, padx=12, pady=10, sticky="nsew")
        card.grid_columnconfigure(0, weight=1)

        ctk.CTkLabel(card, text=title, text_color="#91a4c7").grid(row=0, column=0, padx=14, pady=(12, 0), sticky="w")

        lbl = ctk.CTkLabel(card, text=value, font=ctk.CTkFont(size=24, weight="bold"))
        lbl.grid(row=1, column=0, padx=14, pady=(0, 12), sticky="w")
        return lbl

    def manual_refresh_values(self):
        if NETWORK.refresh_live_values_now():
            STATE.set_status("Manual value refresh succeeded.")
        else:
            STATE.set_status("Manual value refresh failed or no values returned.")

    def save_geometry_loop(self):
        try:
            with STATE.lock:
                STATE.ui_config["geometry"] = self.geometry()
        except Exception:
            pass
        CONFIG.schedule_save()
        self.after(5000, self.save_geometry_loop)

    def save_ui_settings_soon(self):
        try:
            with STATE.lock:
                STATE.ui_config["height_value"] = safe_float(self.height_entry.get(), 1.6)
                STATE.ui_config["smooth_enabled"] = bool(self.main_smooth_enabled.get())
                STATE.ui_config["smooth_time"] = self.main_smooth_time.get()
                STATE.ui_config["geometry"] = self.geometry()
        except Exception:
            pass
        CONFIG.schedule_save()

    def get_main_smooth_time(self):
        return clamp(safe_float(self.main_smooth_time.get(), 0.35), 0.02, 10.0)

    def on_slider(self, value):
        self.height_entry.delete(0, "end")
        self.height_entry.insert(0, f"{float(value):.2f}")
        self.save_ui_settings_soon()

    def set_height_from_entry(self):
        try:
            h = float(self.height_entry.get())
            self.height_var.set(clamp(h, 0.1, 5.0))
            self.save_ui_settings_soon()
            OSC.send_height(h, bool(self.main_smooth_enabled.get()), self.get_main_smooth_time())
        except Exception:
            STATE.set_status("Invalid height value.")

    def add_height(self, delta):
        snap = STATE.snapshot()
        current = snap["eyeheight"]
        if current is None:
            current = safe_float(self.height_entry.get(), 1.6)

        h = clamp(float(current) + float(delta), MIN_OSC_HEIGHT, MAX_OSC_HEIGHT)

        self.height_var.set(clamp(h, 0.1, 5.0))
        self.height_entry.delete(0, "end")
        self.height_entry.insert(0, f"{h:.2f}")
        self.save_ui_settings_soon()

        OSC.send_height(h, bool(self.main_smooth_enabled.get()), self.get_main_smooth_time())

    def add_rule(self):
        with STATE.lock:
            STATE.rules.append(Rule())
        CONFIG.schedule_save()
        self.refresh_rules_ui()

    def remove_rule(self, idx):
        with STATE.lock:
            if 0 <= idx < len(STATE.rules):
                STATE.rules.pop(idx)

        self.expanded_rules = {
            i if i < idx else i - 1
            for i in self.expanded_rules
            if i != idx
        }

        CONFIG.schedule_save()
        self.refresh_rules_ui()

    def toggle_rule_expanded(self, idx):
        if idx in self.expanded_rules:
            self.expanded_rules.remove(idx)
        else:
            self.expanded_rules.add(idx)
        self.refresh_rules_ui()

    def test_rule(self, idx):
        with STATE.lock:
            if not 0 <= idx < len(STATE.rules):
                return
            r = STATE.rules[idx]
        OSC.trigger_rule_action(r, ignore_cooldown=True)

    def refresh_rules_ui(self):
        for w in self.rule_widgets:
            try:
                w.destroy()
            except Exception:
                pass
        self.rule_widgets.clear()

        snap = STATE.snapshot()

        for idx, rule in enumerate(snap["rules"]):
            frame = ctk.CTkFrame(self.scroll, corner_radius=14, fg_color="#151d2e")
            frame.grid(row=idx, column=0, padx=6, pady=6, sticky="ew")
            frame.grid_columnconfigure(2, weight=1)
            self.rule_widgets.append(frame)

            enabled_var = ctk.BooleanVar(value=rule.enabled)
            param_var = ctk.StringVar(value=rule.parameter)
            height_var = ctk.StringVar(value=str(rule.height_value))

            mode_var = ctk.StringVar(value=rule.mode)
            condition_var = ctk.StringVar(value=rule.condition)
            threshold_var = ctk.StringVar(value=str(rule.threshold))
            action_var = ctk.StringVar(value=rule.action)
            cooldown_var = ctk.StringVar(value=str(rule.cooldown))
            edge_var = ctk.BooleanVar(value=rule.rising_edge_only)

            smooth_enabled_var = ctk.BooleanVar(value=rule.smooth_enabled)
            smooth_time_var = ctk.StringVar(value=str(rule.smooth_time))

            limit_enabled_var = ctk.BooleanVar(value=rule.limit_enabled)
            limit_min_var = ctk.StringVar(value=str(rule.limit_min))
            limit_max_var = ctk.StringVar(value=str(rule.limit_max))
            limit_behavior_var = ctk.StringVar(value=rule.limit_behavior)

            follow_input_min_var = ctk.StringVar(value=str(rule.follow_input_min))
            follow_input_max_var = ctk.StringVar(value=str(rule.follow_input_max))
            follow_height_min_var = ctk.StringVar(value=str(rule.follow_height_min))
            follow_height_max_var = ctk.StringVar(value=str(rule.follow_height_max))
            follow_deadband_var = ctk.StringVar(value=str(rule.follow_deadband))

            def bind_update(
                *_,
                i=idx,
                ev=enabled_var,
                pv=param_var,
                hv=height_var,
                mv=mode_var,
                cv=condition_var,
                tv=threshold_var,
                av=action_var,
                cdv=cooldown_var,
                edv=edge_var,
                sev=smooth_enabled_var,
                stv=smooth_time_var,
                lev=limit_enabled_var,
                lminv=limit_min_var,
                lmaxv=limit_max_var,
                lbv=limit_behavior_var,
                fiminv=follow_input_min_var,
                fimaxv=follow_input_max_var,
                fhminv=follow_height_min_var,
                fhmaxv=follow_height_max_var,
                fdv=follow_deadband_var,
            ):
                with STATE.lock:
                    if i >= len(STATE.rules):
                        return
                    r = STATE.rules[i]

                    r.enabled = bool(ev.get())
                    r.parameter = normalize_param_name(pv.get())
                    r.mode = mv.get()
                    r.condition = cv.get()
                    r.action = av.get()
                    r.rising_edge_only = bool(edv.get())
                    r.smooth_enabled = bool(sev.get())
                    r.limit_enabled = bool(lev.get())
                    r.limit_behavior = lbv.get()

                    for attr, var in [
                        ("height_value", hv),
                        ("threshold", tv),
                        ("cooldown", cdv),
                        ("smooth_time", stv),
                        ("limit_min", lminv),
                        ("limit_max", lmaxv),
                        ("follow_input_min", fiminv),
                        ("follow_input_max", fimaxv),
                        ("follow_height_min", fhminv),
                        ("follow_height_max", fhmaxv),
                        ("follow_deadband", fdv),
                    ]:
                        try:
                            setattr(r, attr, float(var.get()))
                        except Exception:
                            pass

                CONFIG.schedule_save()

            all_vars = [
                enabled_var,
                param_var,
                height_var,
                mode_var,
                condition_var,
                threshold_var,
                action_var,
                cooldown_var,
                edge_var,
                smooth_enabled_var,
                smooth_time_var,
                limit_enabled_var,
                limit_min_var,
                limit_max_var,
                limit_behavior_var,
                follow_input_min_var,
                follow_input_max_var,
                follow_height_min_var,
                follow_height_max_var,
                follow_deadband_var,
            ]

            for v in all_vars:
                try:
                    v.trace_add("write", bind_update)
                except Exception:
                    pass

            ctk.CTkCheckBox(frame, text="", variable=enabled_var, width=28).grid(row=0, column=0, padx=(10, 4), pady=10)

            ctk.CTkButton(
                frame,
                text="▼" if idx in self.expanded_rules else "▶",
                width=34,
                command=lambda i=idx: self.toggle_rule_expanded(i),
            ).grid(row=0, column=1, padx=4, pady=10)

            ctk.CTkEntry(
                frame,
                textvariable=param_var,
                placeholder_text="Parameter Name, spaces supported",
            ).grid(row=0, column=2, padx=4, pady=10, sticky="ew")

            ctk.CTkEntry(
                frame,
                textvariable=height_var,
                width=100,
                placeholder_text="Height/Delta",
            ).grid(row=0, column=3, padx=4, pady=10)

            ctk.CTkButton(frame, text="Test", width=58, command=lambda i=idx: self.test_rule(i)).grid(row=0, column=4, padx=4, pady=10)

            ctk.CTkButton(
                frame,
                text="X",
                width=34,
                fg_color="#7c1d2a",
                hover_color="#a3293a",
                command=lambda i=idx: self.remove_rule(i),
            ).grid(row=0, column=5, padx=(4, 10), pady=10)

            if idx not in self.expanded_rules:
                continue

            details = ctk.CTkFrame(frame, fg_color="#101827", corner_radius=12)
            details.grid(row=1, column=0, columnspan=6, padx=10, pady=(0, 10), sticky="ew")
            details.grid_columnconfigure((0, 1, 2, 3, 4, 5), weight=1)

            ctk.CTkLabel(details, text="Mode").grid(row=0, column=0, padx=8, pady=(10, 0), sticky="w")
            ctk.CTkOptionMenu(details, values=["trigger", "follow"], variable=mode_var).grid(row=1, column=0, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Condition").grid(row=0, column=1, padx=8, pady=(10, 0), sticky="w")
            ctk.CTkOptionMenu(details, values=["true", "false", "above", "below"], variable=condition_var).grid(row=1, column=1, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Threshold").grid(row=0, column=2, padx=8, pady=(10, 0), sticky="w")
            ctk.CTkEntry(details, textvariable=threshold_var).grid(row=1, column=2, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Action").grid(row=0, column=3, padx=8, pady=(10, 0), sticky="w")
            ctk.CTkOptionMenu(details, values=["set", "add"], variable=action_var).grid(row=1, column=3, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Cooldown").grid(row=0, column=4, padx=8, pady=(10, 0), sticky="w")
            ctk.CTkEntry(details, textvariable=cooldown_var).grid(row=1, column=4, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkCheckBox(details, text="Edge only", variable=edge_var).grid(row=1, column=5, padx=8, pady=(0, 10), sticky="w")

            ctk.CTkCheckBox(details, text="Smooth this rule", variable=smooth_enabled_var).grid(row=2, column=0, columnspan=2, padx=8, pady=(4, 10), sticky="w")
            ctk.CTkLabel(details, text="Smooth time").grid(row=2, column=2, padx=8, pady=(4, 10), sticky="e")
            ctk.CTkEntry(details, textvariable=smooth_time_var).grid(row=2, column=3, padx=8, pady=(4, 10), sticky="ew")

            ctk.CTkLabel(
                details,
                text="Height Limit Settings",
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color="#a9b8d8",
            ).grid(row=3, column=0, columnspan=6, padx=8, pady=(8, 4), sticky="w")

            ctk.CTkCheckBox(details, text="Enable limits", variable=limit_enabled_var).grid(row=4, column=0, columnspan=2, padx=8, pady=(4, 10), sticky="w")

            ctk.CTkLabel(details, text="Min Height").grid(row=4, column=2, padx=8, pady=(4, 0), sticky="w")
            ctk.CTkEntry(details, textvariable=limit_min_var).grid(row=5, column=2, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Max Height").grid(row=4, column=3, padx=8, pady=(4, 0), sticky="w")
            ctk.CTkEntry(details, textvariable=limit_max_var).grid(row=5, column=3, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(details, text="Limit Behavior").grid(row=4, column=4, padx=8, pady=(4, 0), sticky="w")
            ctk.CTkOptionMenu(
                details,
                values=["clamp", "block_outside", "toward_range"],
                variable=limit_behavior_var,
            ).grid(row=5, column=4, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(
                details,
                text="clamp restricts target. block does nothing outside range. toward only moves back in.",
                text_color="#9db0d0",
                wraplength=260,
            ).grid(row=5, column=5, padx=8, pady=(0, 10), sticky="w")

            ctk.CTkLabel(
                details,
                text="Float Follow Settings",
                font=ctk.CTkFont(size=14, weight="bold"),
                text_color="#a9b8d8",
            ).grid(row=6, column=0, columnspan=6, padx=8, pady=(8, 4), sticky="w")

            labels_vars = [
                ("Input Min", follow_input_min_var),
                ("Input Max", follow_input_max_var),
                ("Height Min", follow_height_min_var),
                ("Height Max", follow_height_max_var),
                ("Deadband", follow_deadband_var),
            ]

            for col, (label, var) in enumerate(labels_vars):
                ctk.CTkLabel(details, text=label).grid(row=7, column=col, padx=8, pady=(4, 0), sticky="w")
                ctk.CTkEntry(details, textvariable=var).grid(row=8, column=col, padx=8, pady=(0, 10), sticky="ew")

            ctk.CTkLabel(
                details,
                text="Set mode to follow. Example: input 0→1 maps height 0.5→2.0m.",
                text_color="#9db0d0",
                wraplength=260,
            ).grid(row=8, column=5, padx=8, pady=(0, 10), sticky="w")

    def update_loop(self):
        while True:
            try:
                UI_EVENTS.get_nowait()
            except queue.Empty:
                break

        snap = STATE.snapshot()

        def fmt_m(v):
            return "-- m" if v is None else f"{float(v):.3f} m"

        self.eye_label.configure(text=fmt_m(snap["eyeheight"]))
        self.min_label.configure(text=fmt_m(snap["eyeheightmin"]))
        self.max_label.configure(text=fmt_m(snap["eyeheightmax"]))

        if snap["scalingallowed"] is None:
            allowed_text = "--"
            allowed_color = "white"
        elif snap["scalingallowed"]:
            allowed_text = "Yes"
            allowed_color = "#7ee787"
        else:
            allowed_text = "No / Blocked"
            allowed_color = "#ff7b72"

        self.allowed_label.configure(text=allowed_text, text_color=allowed_color)

        vrchat = "Connected" if snap["vrchat_found"] else "Searching/Reconnecting"

        self.status_label.configure(
            text=(
                f"{vrchat} | "
                f"Query {snap['vrchat_query_ip']}:{snap['vrchat_query_port']} | "
                f"OSC Target {snap['vrchat_osc_ip']}:{snap['vrchat_osc_port']} | "
                f"Local OSC {snap['local_osc_port']} | "
                f"Local HTTP {snap['local_query_port']} | "
                f"Gen {snap['network_generation']} | "
                f"Hard restarts {snap['hard_restart_count']} | "
                f"Params {len(snap['params'])} | "
                f"{snap['last_status']}"
            )
        )

        self.after(250, self.update_loop)

    def on_close(self):
        self.save_ui_settings_soon()
        CONFIG.save_now()
        self.destroy()


def main():
    CONFIG.load()
    NETWORK.start()

    app = HeightApp()
    app.protocol("WM_DELETE_WINDOW", app.on_close)

    try:
        app.mainloop()
    finally:
        try:
            CONFIG.save_now()
        except Exception:
            pass
        try:
            NETWORK.stop()
        except Exception:
            pass


if __name__ == "__main__":
    main()
