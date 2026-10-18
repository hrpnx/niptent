"""C# スニペットを MCP for Unity の execute_code で Unity Editor に実行させる。

使い方:
  python run_cs.py FILE.cs [--set NAME=VALUE ...] [--instance NAME@HASH] [--timeout 120]

  --set       スニペット冒頭の `const <型> NAME = ...;` の値を差し替える（ファイルは書き換えない）。
              string は自動でクォートする（Windows のパスの \\ もそのまま渡せる）。bool / float / int は C# の書き方で書く。
  --instance  Unity が複数つながっている時に、実行先を選ぶ（`Name@hash`、hash の先頭、ポート番号）。
              つながっている Unity の一覧は `--instances` で出る。

前提: Unity に MCP for Unity（com.coplaydev.unity-mcp）が入っていて、HTTP サーバー
（既定 http://127.0.0.1:8080/mcp、環境変数 UNITY_MCP_URL で変更可）が動いていること。

終了コード: 0 成功 / 1 スニペットの失敗（コンパイルエラー・例外） / 2 サーバーや Unity につながらない
"""
import sys, os, re, json, time, socket, argparse, http.client, urllib.parse

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

URL = os.environ.get("UNITY_MCP_URL", "http://127.0.0.1:8080/mcp")


def post(payload, sid=None, timeout=30):
    """1 リクエスト送る。応答は閉じない SSE のことがあるので、目的の JSON-RPC 応答を得たら閉じる。"""
    u = urllib.parse.urlsplit(URL)
    conn = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=timeout)
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if sid:
        headers["mcp-session-id"] = sid
    conn.request("POST", u.path or "/", body=json.dumps(payload).encode(), headers=headers)
    resp = conn.getresponse()
    new_sid = resp.headers.get("mcp-session-id")
    want = payload.get("id")
    buf = []
    deadline = time.monotonic() + timeout
    try:
        while time.monotonic() < deadline:
            try:
                raw = resp.readline()
            except (TimeoutError, socket.timeout):
                break
            if not raw:
                break
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if line.startswith("data:"):
                buf.append(line[5:].lstrip())
            elif line == "" and buf:
                try:
                    j = json.loads("".join(buf))
                except json.JSONDecodeError:
                    j = None
                buf = []
                if isinstance(j, dict) and ("result" in j or "error" in j) and (want is None or j.get("id") == want):
                    return j, new_sid
            elif line.lstrip().startswith("{"):
                try:
                    return json.loads(line), new_sid
                except json.JSONDecodeError:
                    pass
        if buf:
            try:
                return json.loads("".join(buf)), new_sid
            except json.JSONDecodeError:
                pass
        return None, new_sid
    finally:
        resp.close()
        conn.close()


def handshake():
    _, sid = post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                   "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                              "clientInfo": {"name": "run_cs.py", "version": "1"}}})
    if sid:
        post({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
    return sid


def instances(sid):
    obj, _ = post({"jsonrpc": "2.0", "id": 2, "method": "resources/read",
                   "params": {"uri": "mcpforunity://instances"}}, sid)
    try:
        return obj["result"]["contents"][0]["text"]
    except (KeyError, IndexError, TypeError):
        return json.dumps(obj, ensure_ascii=False)


def _instance_problem(text):
    """実行先の Unity を選べなかった時の応答か（例外文の "instance of an object" には反応しない）。"""
    t = text.lower()
    return "unity instance" in t or "unity_instance" in t or "multiple unity" in t or "no_unity_session" in t


def apply_sets(code, sets):
    for s in sets:
        name, sep, value = s.partition("=")
        if not sep:
            sys.exit(f"--set は NAME=VALUE の形で書く: {s}")
        pat = re.compile(r"^(\s*const\s+(\w+)\s+" + re.escape(name) + r"\s*=\s*)(.*?)(;.*)$", re.M)
        m = pat.search(code)
        if not m:
            sys.exit(f"スニペットに const {name} が無い")
        if m.group(2) == "string":
            value = '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'
        code = code[:m.start()] + m.group(1) + value + m.group(4) + code[m.end():]
    return code


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file", nargs="?")
    ap.add_argument("--set", action="append", default=[])
    ap.add_argument("--instance")
    ap.add_argument("--instances", action="store_true", help="つながっている Unity の一覧を出す")
    ap.add_argument("--timeout", type=int, default=120)
    a = ap.parse_args()

    try:
        sid = handshake()
    except OSError as e:
        print(f"MCP サーバー ({URL}) につながらない: {e}\n"
              "Unity の Window > MCP for Unity でサーバーを起動する。", file=sys.stderr)
        return 2
    if a.instances:
        print(instances(sid))
        return 0
    if not a.file:
        ap.error("FILE が要る")
    with open(a.file, encoding="utf-8") as f:
        code = apply_sets(f.read(), a.set)

    args = {"action": "execute", "code": code}
    if a.instance:
        inst = a.instance
        if "@" not in inst and not inst.isdigit():
            # プロジェクト名だけなら、一覧から Name@hash に直す（サーバーは名前だけでは選べない）
            try:
                listed = json.loads(instances(sid)).get("instances") or []
            except (ValueError, AttributeError):
                listed = []
            ids = [x.get("id") for x in listed if x.get("name") == inst]
            if len(ids) == 1:
                inst = ids[0]
        args["unity_instance"] = inst
    obj, _ = post({"jsonrpc": "2.0", "id": 10, "method": "tools/call",
                   "params": {"name": "execute_code", "arguments": args}}, sid, timeout=a.timeout)
    if obj is None:
        print(f"{a.timeout} 秒以内に応答が無かった。Unity がコンパイル中・取り込み中か、処理が重すぎる。", file=sys.stderr)
        return 2
    try:
        text = obj["result"]["content"][0]["text"]
    except (KeyError, IndexError, TypeError):
        text = json.dumps(obj, ensure_ascii=False)
    try:
        j = json.loads(text)
    except (ValueError, TypeError):
        j = None
    if not isinstance(j, dict):
        print(text, file=sys.stderr)
        if _instance_problem(text):
            print("つながっている Unity:\n" + instances(sid) + "\n--instance で実行先を選ぶ。", file=sys.stderr)
        return 2
    if not j.get("success"):
        print(f"FAILED: {j.get('message') or j.get('error')}", file=sys.stderr)
        data = j.get("data")
        if isinstance(data, dict):
            for e in data.get("errors") or []:
                print(f"  {e}", file=sys.stderr)
            if data.get("stackTrace"):
                print(str(data["stackTrace"])[:1500], file=sys.stderr)
        elif data is not None:
            print(json.dumps(data, ensure_ascii=False)[:1500], file=sys.stderr)
        msg = (j.get("message") or "") + json.dumps(data, ensure_ascii=False)
        if _instance_problem(msg):
            print("つながっている Unity:\n" + instances(sid) + "\n--instance で実行先を選ぶ。", file=sys.stderr)
            return 2
        return 1
    result = (j.get("data") or {}).get("result")
    print(result if isinstance(result, str) else json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
