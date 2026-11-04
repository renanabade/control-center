"""Busca as lampadas da conta pelo login QR do app Smart Life e grava em %APPDATA%\\win-control\\lamp_config.json.

Uso (num terminal normal do Windows, NAO de dentro de outro app como o Claude: veja docs/lampada.md):
    py -m pip install -r tools/requirements.txt
    py tools/pegar_chave.py [USER_CODE]

O User Code fica no Smart Life em Eu > engrenagem > Conta e Seguranca > User Code.
Rode uma vez por conta: as lampadas se somam, e quem ja estava na config mantem IP, MAC e redes.
As chaves nunca sao impressas; o login nao fica salvo.
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
# categorias de iluminacao da Tuya: lampada, fita de LED, teto, abajur e dimmer
CATEGORIAS_LUZ = {"dj", "dd", "dc", "xdd", "fwd", "fsd", "tyndj", "tgq", "tgkg", "sxd", "ykq"}
MARCADOR_ID = "SEU_DEVICE_ID"

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

aparelhos = list(manager.device_map.values())
if not aparelhos:
    raise SystemExit("Nenhum dispositivo nessa conta.")
lampadas = [d for d in aparelhos if d.category in CATEGORIAS_LUZ]
if not lampadas:
    raise SystemExit("Nenhuma lampada nessa conta. Categorias encontradas: " + ", ".join(sorted({d.category for d in aparelhos})))

os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
cfg = {}
if os.path.exists(CONFIG):
    with open(CONFIG, encoding="utf-8-sig") as f:
        cfg = json.load(f)

lista = cfg.get("lampadas") or []
# formato antigo, de uma lampada so: entra na lista em vez de se perder
if cfg.get("id") and cfg["id"] != MARCADOR_ID and not any(x.get("id") == cfg["id"] for x in lista):
    lista.append({k: cfg[k] for k in ("id", "local_key", "ip", "version", "ble_mac") if k in cfg})
for k in ("id", "local_key", "ip", "version", "ble_mac"):
    cfg.pop(k, None)

for d in lampadas:
    item = next((x for x in lista if x.get("id") == d.id), None)
    if item is None:
        item = {"id": d.id}
        lista.append(item)
    item.update({"nome": d.name, "local_key": d.local_key, "categoria": d.category})
    # dp -> codigo: nem todo modelo usa os mesmos numeros (20 liga/desliga, 22 brilho...)
    estrategia = getattr(d, "local_strategy", None) or {}
    item["dps"] = {str(n): e.get("status_code", "") for n, e in estrategia.items() if isinstance(e, dict)}
    item.setdefault("ip", "")          # vazio: o app acha o IP sozinho pelo aviso UDP da lampada
    item.setdefault("version", 3.3)
    item.setdefault("ble_mac", "")
    print(f"  {d.name} ({d.id})")

cfg["lampadas"] = lista
with open(CONFIG, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2, ensure_ascii=False)
print(f"Pronto: {len(lampadas)} lampada(s) gravada(s) em {CONFIG}.")
