"""Exercise the real stdio MCP SDK against an owned, simulated DeltaNFD host."""
import argparse
import ctypes
from ctypes import wintypes
import json
import msvcrt
import os
from pathlib import Path
import queue
import re
import shutil
import struct
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--mcp', type=Path, required=True)
    parser.add_argument('--fixture-build', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    host_dir = args.output / 'owned-host'
    host_dir.mkdir(exist_ok=True)
    for source in args.fixture_build.iterdir():
        if source.is_file():
            shutil.copy2(source, host_dir / source.name)
    shutil.copy2(host_dir / 'McpBridgeChecks.exe', host_dir / 'DeltaNFD.exe')
    passed = []

    def check(condition, name):
        if not condition:
            raise AssertionError(name)
        passed.append(name)

    host_log = (args.output / 'host-stderr.log').open('w', encoding='utf-8')
    mcp_log = (args.output / 'mcp-stderr.log').open('w', encoding='utf-8')
    host = subprocess.Popen([str(host_dir / 'DeltaNFD.exe'), '--fixture'], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=host_log, text=True, encoding='utf-8')
    mcp = None
    try:
        ready = host.stdout.readline().strip()
        check(ready.startswith('READY '), 'owned fixture started')
        endpoint_path = Path(ready[6:])
        empty_runtime = args.output / 'empty-runtime'
        empty_runtime.mkdir(exist_ok=True)
        mcp_env = os.environ.copy()
        mcp_env.update({'DOTNET_ROOT': str(empty_runtime.resolve()), 'DOTNET_ROOT_X64': str(empty_runtime.resolve()),
                        'PATH': '', 'DOTNET_DISABLE_GUI_ERRORS': '1'})
        mcp = subprocess.Popen([str(args.mcp.resolve()), '--app-dir', str(host_dir.resolve())], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=mcp_log, text=True, encoding='utf-8', bufsize=1, env=mcp_env)
        lines = queue.Queue()

        def read_stdout():
            for line in mcp.stdout:
                lines.put(line)
        threading.Thread(target=read_stdout, daemon=True).start()
        next_id = 0

        def call(method, params):
            nonlocal next_id
            next_id += 1
            mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': next_id, 'method': method, 'params': params}) + '\n')
            mcp.stdin.flush()
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                line = lines.get(timeout=max(0.1, deadline - time.monotonic()))
                message = json.loads(line)  # Any stdout log line is a protocol failure.
                if message.get('id') == next_id:
                    if 'error' in message:
                        raise RuntimeError(message['error'])
                    return message['result']
            raise TimeoutError(method)

        def tool(name, arguments):
            return call('tools/call', {'name': name, 'arguments': arguments})

        def value(result):
            if result.get('isError'):
                raise AssertionError(result)
            if 'structuredContent' in result:
                content = result['structuredContent']
                # SDK result types may wrap scalar values; normal object results are unwrapped.
                return content
            return json.loads(''.join(c['text'] for c in result['content'] if c['type'] == 'text'))

        init = call('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                   'clientInfo': {'name': 'DeltaNFD-owned-checks', 'version': '1'}})
        check('tools' in init['capabilities'], 'SDK initialize and tools capability')
        mcp.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); mcp.stdin.flush()
        tools = call('tools/list', {})['tools']
        check({t['name'] for t in tools} == {'deltanfd_status', 'deltanfd_list_actions', 'deltanfd_submit_action', 'deltanfd_get_operation', 'deltanfd_cancel_operation'}, 'five actual SDK tools')
        catalog = value(tool('deltanfd_list_actions', {}))
        check(isinstance(catalog, list) and len(catalog) == 112, '112 action schemas through SDK')
        check(all('inputSchema' in action for action in catalog), 'schemas available to agent')
        gpu = value(tool('deltanfd_list_actions', {'module': 'gpu'}))
        check(gpu and all(a['module'] == 'gpu' for a in gpu), 'module filter')
        fixture_status = value(tool('deltanfd_status', {}))
        check(fixture_status['simulated'], 'authenticated native pipe status')
        operation = value(tool('deltanfd_submit_action', {'action': 'app.status', 'arguments': {}, 'requestId': 'e2e-read'}))
        check('id' in operation, 'operation ID returned')
        for _ in range(100):
            current = value(tool('deltanfd_get_operation', {'operationId': operation['id']}))
            if current['state'] == 'completed':
                break
            time.sleep(0.02)
        check(current['state'] == 'completed' and current['result']['data']['simulated'], 'query complete with original result')
        duplicate = value(tool('deltanfd_submit_action', {'action': 'app.status', 'arguments': {}, 'requestId': 'e2e-read'}))
        check(duplicate['id'] == operation['id'], 'retry deduplicated through SDK')
        cancelled = value(tool('deltanfd_cancel_operation', {'operationId': operation['id']}))
        check(cancelled['state'] == 'completed', 'cancel tool preserves finished operation')
        conflict = tool('deltanfd_submit_action', {'action': 'gpu.driver.status', 'arguments': {}, 'requestId': 'e2e-read'})
        check(conflict.get('isError'), 'conflicting retry rejects')
        check('requestId' in json.dumps(conflict, ensure_ascii=False), 'agent receives actionable conflict reason')
        forbidden = tool('deltanfd_submit_action', {'action': 'procman.clock.apply', 'arguments': {}, 'requestId': 'forbidden'})
        check(forbidden.get('isError'), 'hidden feature rejects through SDK')
        wrong_type = tool('deltanfd_submit_action', {'action': 'system.power.hibernate.set', 'arguments': {'enable': 'false'}, 'requestId': 'wrong-type'})
        check(wrong_type.get('isError'), 'argument type rejects through SDK')
        fake_confirm = tool('deltanfd_submit_action', {'action': 'gpu.driver.apply', 'arguments': {'changes': [], 'rebarWarningAccepted': True}, 'requestId': 'fake-confirm'})
        check(fake_confirm.get('isError'), 'agent cannot supply local consent')

        endpoint_bytes = endpoint_path.read_bytes()
        endpoint = json.loads(endpoint_bytes)
        with open('\\\\.\\pipe\\' + endpoint['pipe'], 'r+b', buffering=0) as pipe:
            advapi = ctypes.WinDLL('advapi32', use_last_error=True)
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            security = ctypes.c_void_p()
            get_security = advapi.GetSecurityInfo
            get_security.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD,
                                     ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                     ctypes.POINTER(ctypes.c_void_p)]
            get_security.restype = wintypes.DWORD
            convert = advapi.ConvertSecurityDescriptorToStringSecurityDescriptorW
            convert.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD,
                                ctypes.POINTER(wintypes.LPWSTR), ctypes.c_void_p]
            convert.restype = wintypes.BOOL
            kernel.LocalFree.argtypes = [ctypes.c_void_p]; kernel.LocalFree.restype = ctypes.c_void_p
            check(get_security(msvcrt.get_osfhandle(pipe.fileno()), 6, 0x15, None, None, None, None,
                               ctypes.byref(security)) == 0, 'actual pipe security readable')
            sddl = wintypes.LPWSTR()
            try:
                check(bool(convert(security, 1, 0x15, ctypes.byref(sddl), None)), 'actual pipe descriptor decodes')
                descriptor = sddl.value
                owner = descriptor.split('D:')[0][2:]
                check(owner == fixture_status['userSid'] and
                      re.search(r'\(A;;[^;]+;;;' + re.escape(owner) + r'\)', descriptor) and
                      descriptor.count('(A;') == 1 and '(D;;' in descriptor and ';;;NU)' in descriptor,
                      'explicit user SID is sole allow rule and network denied')
                check('(ML;;NW;;;ME)' in descriptor, 'medium integrity channel for ordinary Agent')
                (args.output / 'pipe-security.txt').write_text(descriptor, encoding='utf-8')
            finally:
                if sddl: kernel.LocalFree(ctypes.cast(sddl, ctypes.c_void_p))
                kernel.LocalFree(security)
            payload = json.dumps({'token': '0' * 64, 'method': 'status', 'arguments': {}}).encode()
            pipe.write(struct.pack('<i', len(payload)) + payload)
            size = struct.unpack('<i', pipe.read(4))[0]
            response = json.loads(pipe.read(size))
            check(not response['success'] and '凭据' in response['error'], 'wrong session token rejects')
        try:
            altered = dict(endpoint); altered['pid'] = os.getpid()
            endpoint_path.write_text(json.dumps(altered), encoding='utf-8')
            check(tool('deltanfd_status', {}).get('isError'), 'actual pipe server PID verified')
            altered = dict(endpoint); altered['startTicks'] += 1
            endpoint_path.write_text(json.dumps(altered), encoding='utf-8')
            check(tool('deltanfd_status', {}).get('isError'), 'limited process query verifies creation time')
        finally:
            endpoint_path.write_bytes(endpoint_bytes)
        check(value(tool('deltanfd_status', {}))['simulated'], 'healthy after rejected requests')
        host.stdin.write('\n'); host.stdin.flush(); host.wait(timeout=10)
        check(not endpoint_path.exists(), 'endpoint removed on clean disable')
        disabled = tool('deltanfd_status', {})
        check(disabled.get('isError'), 'disabled application returns MCP error')
        check('开启本次 MCP 接入' in json.dumps(disabled, ensure_ascii=False), 'disabled error explains how to connect')
        shutil.copy2(host_dir / 'McpBridgeChecks.exe', host_dir / 'WrongImage.exe')
        host = subprocess.Popen([str(host_dir / 'WrongImage.exe'), '--fixture'], stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=host_log, text=True, encoding='utf-8')
        check(host.stdout.readline().startswith('READY '), 'wrong-image fixture started')
        check(tool('deltanfd_status', {}).get('isError'), 'limited process query rejects wrong image')
        host.stdin.write('\n'); host.stdin.flush(); host.wait(timeout=10)
        (args.output / 'result.json').write_text(json.dumps({'passed': len(passed), 'checks': passed,
            'realMcpSdk': True, 'realWindowsPipes': True, 'simulatedApplication': True,
            'productionGui': False, 'mcpWithoutSystemDotnet': True, 'crossUacObserved': False,
            'explicitUserAclObserved': True, 'mediumPipeLabelObserved': True,
            'systemWrites': False, 'gpuWrites': False}, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps({'passed': len(passed), 'systemWrites': False, 'gpuWrites': False}))
    finally:
        if mcp is not None:
            if mcp.stdin: mcp.stdin.close()
            try: mcp.wait(timeout=8)
            except subprocess.TimeoutExpired: mcp.terminate(); mcp.wait(timeout=5)
        if host.poll() is None:
            try: host.stdin.write('\n'); host.stdin.flush(); host.wait(timeout=8)
            except Exception: host.terminate(); host.wait(timeout=5)
        host_log.close(); mcp_log.close()


if __name__ == '__main__':
    main()
