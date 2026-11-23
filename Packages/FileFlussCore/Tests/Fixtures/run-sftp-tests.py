import os
import pathlib
import pwd
import socket
import subprocess
import sys
import tempfile
import time

package = pathlib.Path(__file__).resolve().parents[2]

with tempfile.TemporaryDirectory(prefix="filefluss-test.", dir="/tmp") as directory:
    root = pathlib.Path(directory)
    username = pwd.getpwuid(os.getuid()).pw_name
    for key, passphrase in (("server", ""), ("other-server", ""), ("plain", ""), ("encrypted", "test key passphrase"), ("agent", "")):
        subprocess.run(["/usr/bin/ssh-keygen", "-q", "-t", "ed25519", "-N", passphrase, "-f", str(root / key)], check=True)
    (root / "authorized_keys").write_text("".join((root / (key + ".pub")).read_text() for key in ("plain", "encrypted", "agent")))
    (root / "sample.txt").write_text("FileFluss SFTP test fixture\n")
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    for name, key in (("known_hosts", "server"), ("changed_hosts", "other-server")):
        (root / name).write_text(f"[127.0.0.1]:{port} " + (root / (key + ".pub")).read_text())
    (root / "unknown_hosts").touch()
    config = root / "sshd_config"
    config.write_text(f"""Port {port}
ListenAddress 127.0.0.1
HostKey {root / "server"}
PidFile {root / "sshd.pid"}
AuthorizedKeysFile {root / "authorized_keys"}
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes no
AllowUsers {username}
Subsystem sftp /usr/libexec/sftp-server
""")
    environment = dict(os.environ, SSH_AUTH_SOCK=str(root / "agent.sock"))
    with (root / "sshd.log").open("w") as log:
        server = subprocess.Popen(["/usr/sbin/sshd", "-D", "-e", "-f", str(config)], stderr=log)
        agent = subprocess.Popen(["/usr/bin/ssh-agent", "-D", "-a", environment["SSH_AUTH_SOCK"]], stdout=subprocess.DEVNULL)
        try:
            for _ in range(50):
                if server.poll() is not None or agent.poll() is not None:
                    raise RuntimeError("Disposable OpenSSH process exited.\n" + (root / "sshd.log").read_text())
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=0.1):
                        if (root / "agent.sock").exists():
                            break
                except OSError:
                    pass
                time.sleep(0.1)
            else:
                raise RuntimeError("Disposable OpenSSH processes did not start")
            subprocess.run(["/usr/bin/ssh-add", "-q", str(root / "agent")], env=environment, check=True)
            environment.update(FILEFLUSS_TEST_SFTP_ROOT=str(root), FILEFLUSS_TEST_SFTP_PORT=str(port))
            subprocess.run([
                "swift", "test", "--package-path", str(package),
                "--filter", "SFTPSecurityTests", *sys.argv[1:],
            ], env=environment, check=True)
        finally:
            for process in (server, agent):
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=10)
