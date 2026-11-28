"""Minimal socket lifecycle adaptation of the pinned MIT upstream sender."""
from pathlib import Path
import sys

path = Path(sys.argv[1])
code = path.read_text(encoding="utf-8")
steps = [
    ("    on_progress: Optional[Callable[[SendProgress], None]] = None,\n",
     "    on_progress: Optional[Callable[[SendProgress], None]] = None,\n"
     "    on_socket: Optional[Callable[[socket.socket], None]] = None,\n"
     "    source_address: str | None = None,\n"),
    ('        debug.log("send", f"connecting to {address}:{port}")\n',
     '        sock.settimeout(50)\n'
     '        if source_address is not None:\n'
     '            sock.bind((source_address, 0))\n'
     '        if on_socket is not None:\n'
     '            on_socket(sock)\n'
     '        debug.log("send", f"connecting to {address}:{port}")\n'),
]
for original, replacement in steps:
    if code.count(original) != 1:
        raise SystemExit("Pinned Quick Share sender does not match the expected source.")
    code = code.replace(original, replacement, 1)
path.write_text(code, encoding="utf-8")
