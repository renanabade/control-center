"""Busca id e local_key da lampada pelo login QR do app Smart Life e grava em %APPDATA%\\win-control\\lamp_config.json.

Uso (num terminal normal do Windows, NAO de dentro de outro app como o Claude: veja docs/lampada.md):
    py -m pip install -r tools/requirements.txt
    py tools/pegar_chave.py [USER_CODE]

O User Code fica no Smart Life em Eu > engrenagem > Conta e Seguranca > User Code.
A chave nunca e impressa; o login nao fica salvo.
"""
import json
import os
import sys
import time

import qrcode
from tuya_sharing import LoginControl, Manager

CLIENT_ID = "HA_3y9q4ak7g4ephrvke"
SCHEMA = "haauthorize"
CONFIG = os.path.join(os.environ["APPDATA"], "win-control", "lamp_config.json")

user_code = sys.argv[1] if len(sys.argv) > 1 else input("User Code (Smart Life > Eu > engrenagem > Conta e Seguranca): ").strip()

lc = LoginControl()
resp = lc.qr_code(CLIENT_ID, SCHEMA, user_code)
if not resp.get("success"):
    raise SystemExit(f"Falha ao gerar o QR: {resp}")
token = resp["result"]["qrcode"]
qr_path = os.path.join(os.environ.get("TEMP", "."), "control-center-qr.png")
qrcode.make(f"tuyaSmart--qrLogin?token={token}").save(qr_path)
os.startfile(qr_path)
print("Escaneie o QR com o Smart Life (Eu > icone de scan) e confirme no celular...", flush=True)

info = None
for _ in range(60):
    ok, data = lc.login_result(token, CLIENT_ID, user_code)
    if ok:
        info = data
        break
    time.sleep(3)
os.remove(qr_path)
if not info:
    raise SystemExit("Tempo esgotado esperando o login.")

manager = Manager(CLIENT_ID, user_code, info["terminal_id"], info["endpoint"], {
    "t": info.get("t"), "uid": info.get("uid"), "expire_time": info.get("expire_time"),
    "access_token": info.get("access_token"), "refresh_token": info.get("refresh_token"),
})
manager.update_device_cache()

# lampadas sao a categoria "dj"; com mais de uma, pergunta qual
lamps = [d for d in manager.device_map.values() if d.category == "dj"] or list(manager.device_map.values())
if not lamps:
    raise SystemExit("Nenhum dispositivo nessa conta.")
dev = lamps[0]
if len(lamps) > 1:
    for i, d in enumerate(lamps):
        print(f"  {i}: {d.name} ({d.id})")
    dev = lamps[int(input("Qual lampada? ").strip())]

os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
cfg = {}
if os.path.exists(CONFIG):
    with open(CONFIG, encoding="utf-8-sig") as f:
        cfg = json.load(f)
cfg.update({"id": dev.id, "local_key": dev.local_key})
cfg.setdefault("ip", "")          # vazio: o app acha o IP sozinho pelo aviso UDP da lampada
cfg.setdefault("version", 3.3)
with open(CONFIG, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
print(f"Pronto: {dev.name} gravada em {CONFIG}. Reinicie o Control Center.")
