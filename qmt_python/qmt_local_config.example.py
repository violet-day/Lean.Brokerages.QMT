# coding: gbk

# Keep the real account identifier in qmt_local_config.py. That file is ignored
# by Git and remains on the Windows machine across repository updates.
ACCOUNT_ID = ""

# Read-only instrument used by contract/history/quote probes.
PROBE_STOCK_CODE = "000001.SZ"

# subscribe_quote is read-only. Set False when only query APIs should be tested.
SUBSCRIBE_TICKS = True

# Gateway network settings. Keep loopback unless a protected container bridge
# requires a different host binding.
GATEWAY_BIND_HOST = "127.0.0.1"
GATEWAY_BIND_PORT = 17890

# Non-loopback binding exposes an unauthenticated plaintext protocol. It is
# rejected unless this explicit opt-in is enabled and a firewall restricts it.
GATEWAY_ALLOW_REMOTE_CLIENTS = False

# Safety default. Do not enable until simulation-account validation is complete.
TRADING_ENABLED = False
GATEWAY_STRATEGY_NAME = "LeanQmtGateway"
