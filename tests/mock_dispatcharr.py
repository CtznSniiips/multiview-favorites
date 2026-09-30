"""Serve the REAL dispatcharr-multiview dash/api.py handlers over WSGI with the
Django/ORM bits stubbed, so the Emby plugin's client can be exercised against
the actual request handling, PATCH semantics and layout reconcile logic.

Usage: python3 mock_dispatcharr.py <path-to-dispatcharr-multiview/src> <port> <state.json>
"""
import importlib.util
import json
import os
import sys
import types
from wsgiref.simple_server import make_server, WSGIRequestHandler

SRC, PORT, STATE_PATH = sys.argv[1], int(sys.argv[2]), sys.argv[3]

# --------------------------------------------------------------- stubs
USER, PASSWORD, TOKEN = "admin", "secret", "tok-123"
DB = {"settings": {}}
CHANNELS = [  # (id, name, channel_number)
    (11, "ABC", 7.0), (12, "CBS", 2.0), (13, "NBC", 4.0), (14, "FOX", 5.0),
    (15, "ESPN", 206.0), (16, "ESPN2", 209.0), (17, "PBS Kids", 5.1),
    (99, "Old Multiview", 9000.0),
]
CALLS = []


def mod(name, **attrs):
    m = types.ModuleType(name)
    m.__dict__.update(attrs)
    sys.modules[name] = m
    return m


class _Tok:
    def __init__(self, t=None):
        if t is not None and t != TOKEN:
            raise Exception("bad token")
    def __str__(self):
        return TOKEN
    @property
    def access_token(self):
        return TOKEN
    @classmethod
    def for_user(cls, u):
        return cls()


class TokenError(Exception):
    pass


for pkg in ("rest_framework_simplejwt", "django", "django.contrib", "apps", "apps.plugins", "apps.channels", "apps.m3u"):
    mod(pkg)
mod("rest_framework_simplejwt.tokens", AccessToken=_Tok, RefreshToken=_Tok)
mod("rest_framework_simplejwt.exceptions", TokenError=TokenError)
mod("django.contrib.auth", authenticate=lambda username, password: object() if (username, password) == (USER, PASSWORD) else None)


class _Cfg:
    @property
    def settings(self):
        return DB["settings"]
    @settings.setter
    def settings(self, v):
        DB["settings"] = v
    def save(self):
        with open(STATE_PATH, "w") as f:
            json.dump(DB["settings"], f, indent=1, sort_keys=True)


class _PCObjects:
    def get(self, key):
        return _CFG


_CFG = _Cfg()
mod("apps.plugins.models", PluginConfig=type("PluginConfig", (), {"objects": _PCObjects()}))


class _QS:
    def __init__(self, rows):
        self.rows = rows
    def order_by(self, *_):
        return _QS(sorted(self.rows, key=lambda r: r["channel_number"]))
    def values(self, *_):
        return self
    def distinct(self):
        return self
    def filter(self, **kw):
        raise Exception("no such field")
    def __iter__(self):
        return iter(self.rows)


class _ChObjects:
    def order_by(self, *a):
        return _QS([{"id": i, "name": n, "channel_number": c} for i, n, c in CHANNELS]).order_by()
    def filter(self, **kw):
        raise Exception("no such field")


mod("apps.channels.models", Channel=type("Channel", (), {"objects": _ChObjects()}))


class _M3UObjects:
    def filter(self, **kw):
        return types.SimpleNamespace(first=lambda: None)


mod("apps.m3u.models", M3UAccount=type("M3UAccount", (), {"objects": _M3UObjects()}))

# --------------------------------------------------------------- real plugin code
pkg = types.ModuleType("mvplugin")
pkg.__path__ = [SRC]
pkg.PLUGIN_DB_KEY = "multiview"


class _Plugin:
    def _generate_m3u(self):
        CALLS.append("refresh")
        return {"status": "success", "message": "mock m3u regenerated"}


pkg.Plugin = _Plugin
sys.modules["mvplugin"] = pkg

spec = importlib.util.spec_from_file_location("mvplugin.config", os.path.join(SRC, "config.py"))
config = importlib.util.module_from_spec(spec)
config.__package__ = "mvplugin"
sys.modules["mvplugin.config"] = config
spec.loader.exec_module(config)

spec = importlib.util.spec_from_file_location("mvplugin.dash_api", os.path.join(SRC, "dash", "api.py"))
api = importlib.util.module_from_spec(spec)
sys.modules["mvplugin.dash_api"] = api
spec.loader.exec_module(api)


class _Server:
    def kill_stream(self, n):
        CALLS.append(f"restart:{n}")
        return 1
    def kill_active_streams(self):
        return 0
    def reconnect_channel(self, n, i):
        return True
    def get_active_streams(self):
        return []


# Seed: an existing unrelated layout, like a real install.
DB["settings"], _ = config.ensure_layout_order({})
first = DB["settings"]["multiview_order"][0]
DB["settings"].update({
    f"multiview_{first}_name": "Sports Wall",
    f"multiview_{first}_channel_count": 2,
    f"multiview_{first}_channel_1": "15",
    f"multiview_{first}_channel_2": "16",
    "dash_enabled": "enabled",
})
_CFG.save()


def app(environ, start_response):
    path = environ.get("PATH_INFO", "")
    if path == "/__calls":
        body = json.dumps(CALLS).encode()
        start_response("200 OK", [("Content-Type", "application/json")])
        return [body]
    if path == "/__resolve":
        # Mirror server._resolve_layout's classic-mode logic against current settings.
        from urllib.parse import parse_qs
        lid = parse_qs(environ.get("QUERY_STRING", ""))["id"][0]
        s = api._get_settings()
        count = max(2, int(s.get(f"multiview_{lid}_channel_count", 4)))
        ids = {str(i): n for i, n, _ in CHANNELS}
        tiles = [ids[s[f"multiview_{lid}_channel_{m}"]] for m in range(1, count + 1)
                 if s.get(f"multiview_{lid}_channel_{m}", "_none") not in ("_none", "", None)
                 and s.get(f"multiview_{lid}_channel_{m}") in ids]
        fields = [f["id"] for f in config.build_plugin_fields(s) if f["id"].startswith(f"multiview_{lid}_")]
        body = json.dumps({"tiles": tiles, "playable": len(tiles) >= 2, "fields": fields,
                           "order": s["multiview_order"], "count": s.get("multiview_count")}).encode()
        start_response("200 OK", [("Content-Type", "application/json")])
        return [body]

    dash = "/dash"
    if not path.startswith(dash + "/api/"):
        start_response("404 Not Found", [("Content-Type", "text/plain")])
        return [b"Not Found\n"]
    sub = path[len(dash):]
    api._server = _Server()
    routes = {
        "/api/auth/token": api.handle_auth_token,
        "/api/config": api.handle_config,
        "/api/channels": api.handle_channels,
        "/api/refresh": api.handle_refresh,
        "/api/streams/restart": api.handle_streams_restart,
    }
    h = routes.get(sub)
    if not h:
        start_response("404 Not Found", [("Content-Type", "text/plain")])
        return [b"Not Found\n"]
    return h(environ, start_response)


class Quiet(WSGIRequestHandler):
    def log_message(self, *a):
        pass


print("ready", flush=True)
make_server("127.0.0.1", PORT, app, handler_class=Quiet).serve_forever()
