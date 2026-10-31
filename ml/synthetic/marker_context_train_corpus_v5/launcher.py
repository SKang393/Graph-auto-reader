# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Windows pre-import lock/verification launcher; use an independently fixed SHA.

Call with isolated Python. Standard-library imports happen before project source
verification; this is not OS or interpreter attestation.
"""
import ctypes
import hashlib
import importlib
import importlib.machinery
import json
import os
from pathlib import Path
import sys
import time

ROOT = Path(r'E:\Graph-auto-reader')
BINDING = ROOT / 'artifacts/goal22-runs/marker-context-train-corpus-binding-v5.json'
DEV_FILES = {
    'manifest': ROOT / 'artifacts/goal22-runs/marker-context-dev-raster-v2/raster-only/manifest.json',
    'identity': ROOT / 'artifacts/goal22-runs/marker-context-dev-raster-v2/identity-metadata.json',
    'report': ROOT / 'artifacts/goal22-runs/marker-context-dev-import-v1/report.json',
}
OUTPUT = ROOT / 'artifacts/goal22-runs/marker-context-train-corpus-v5-full'
SMOKE_FILES = {
    'receipt': ROOT / 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/COMPLETE.json',
    'manifest': ROOT / 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/raster-only/manifest.json',
    'identity': ROOT / 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/identity-metadata.json',
}


def sha(raw): return hashlib.sha256(raw).hexdigest()
def canonical(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()


def verify_then_import(binding_bytes, expected_sha, lock_source, import_producer):
    """Lock and hash every listed source before calling the injected importer."""
    if type(binding_bytes) is not bytes or sha(binding_bytes) != expected_sha:
        raise ValueError('Prebound source binding differs')
    binding = json.loads(binding_bytes)
    if canonical(binding) != binding_bytes or binding.get('schema') != 'marker-context-train-corpus-binding-v5' or \
       binding.get('revision') != 'marker-context-train-corpus-v5' or binding.get('training_approved') is not False:
        raise ValueError('Train source binding scope differs')
    sources = binding.get('sources')
    if not isinstance(sources, dict) or not sources:
        raise ValueError('Source inventory missing')
    held = []
    try:
        for relative, expected in sorted(sources.items()):
            if not isinstance(relative, str) or not relative or '\\' in relative or relative.startswith('/') or \
               any(p in ('', '.', '..') for p in relative.split('/')) or ':' in relative:
                raise ValueError('Noncanonical source path')
            stream = lock_source(relative)
            held.append(stream)
            if sha(stream.read()) != expected: raise ValueError('Source file changed')
            stream.seek(0)
        producer = import_producer()
        if canonical(producer.source_binding()) != binding_bytes:
            raise ValueError('Imported producer source binding differs')
        return producer, held
    except BaseException:
        for stream in held: stream.close()
        raise


def _no_reparse(path):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
    kernel.GetFileAttributesW.restype = ctypes.c_uint32
    cursor = Path(path.anchor)
    for part in path.parts[1:]:
        cursor /= part
        flags = kernel.GetFileAttributesW(str(cursor))
        if flags == 0xffffffff: raise OSError(ctypes.get_last_error(), 'Cannot inspect bound path')
        if flags & 0x400: raise ValueError('Reparse path denied')


def _locked_read(path):
    if os.name != 'nt': raise OSError('Windows no-writer file locking is required')
    path = Path(path)
    if not path.is_absolute() or not path.is_relative_to(ROOT): raise ValueError('Project path required')
    _no_reparse(path)
    import msvcrt
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                   ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0x00200080, None)
    if handle == ctypes.c_void_p(-1).value: raise OSError(ctypes.get_last_error(), 'Cannot lock bound file')
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    except BaseException:
        kernel.CloseHandle(ctypes.c_void_p(handle)); raise
    return os.fdopen(fd, 'rb')


def _source_only_loader(allowed):
    """Compile bound project sources directly, never project bytecode caches."""
    original = importlib.machinery.SourceFileLoader.get_code
    def get_code(loader, name):
        path = Path(loader.get_filename(name))
        try:
            relative = path.relative_to(ROOT).as_posix()
        except ValueError:
            return original(loader, name)
        expected = allowed.get(relative)
        if expected is None: raise ImportError('Project source absent from binding')
        data = loader.get_data(str(path))
        if sha(data) != expected: raise ImportError('Locked project source differs')
        return loader.source_to_code(data, str(path))
    return original, get_code


def _peak_working_set():
    class Counters(ctypes.Structure):
        _fields_ = [('cb', ctypes.c_uint32), ('PageFaultCount', ctypes.c_uint32),
                    ('PeakWorkingSetSize', ctypes.c_size_t), ('WorkingSetSize', ctypes.c_size_t),
                    ('QuotaPeakPagedPoolUsage', ctypes.c_size_t), ('QuotaPagedPoolUsage', ctypes.c_size_t),
                    ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t), ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                    ('PagefileUsage', ctypes.c_size_t), ('PeakPagefileUsage', ctypes.c_size_t)]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    psapi = ctypes.WinDLL('psapi', use_last_error=True)
    psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_uint32]
    psapi.GetProcessMemoryInfo.restype = ctypes.c_int
    counters = Counters(); counters.cb = ctypes.sizeof(counters)
    if not psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise OSError(ctypes.get_last_error(), 'Cannot read process peak working set')
    return counters.PeakWorkingSetSize


def _postbuild_accounting(producer, result, identity_bytes):
    if not isinstance(result, dict) or sha(identity_bytes) != result.get('identity_sha256'):
        raise ValueError('Post-build identity receipt differs')
    identity = json.loads(identity_bytes)
    return producer.plan.failure_account(identity['sources'], None)


def main():
    if os.name != 'nt': raise OSError('Windows no-writer file locking is required')
    if not sys.flags.isolated: raise ValueError('Launch Python with -I')
    if len(sys.argv) != 2 or len(sys.argv[1]) != 64 or any(c not in '0123456789abcdef' for c in sys.argv[1]):
        raise ValueError('One independently fixed lowercase source binding SHA is required')
    from contextlib import ExitStack
    with ExitStack() as stack:
        binding_stream = stack.enter_context(_locked_read(BINDING))
        dev_bytes = {key: stack.enter_context(_locked_read(path)).read() for key, path in DEV_FILES.items()}
        smoke_bytes = {key: stack.enter_context(_locked_read(path)).read() for key, path in SMOKE_FILES.items()}
        binding_bytes = binding_stream.read()
        for name, module in tuple(sys.modules.items()):
            filename = getattr(module, '__file__', None)
            if name != '__main__' and isinstance(filename, str) and Path(filename).is_absolute() and Path(filename).is_relative_to(ROOT):
                raise ValueError('Project code was loaded before verification')
        saved_loader = importlib.machinery.SourceFileLoader.get_code
        def import_producer():
            allowed = json.loads(binding_bytes)['sources']
            _, safe_loader = _source_only_loader(allowed)
            importlib.machinery.SourceFileLoader.get_code = safe_loader
            sys.path.insert(0, str(ROOT))
            return importlib.import_module('ml.synthetic.marker_context_train_corpus_v5.builder')
        try:
            producer, locks = verify_then_import(binding_bytes, sys.argv[1], lambda p: _locked_read(ROOT / p), import_producer)
            result = None
            try:
                _no_reparse(OUTPUT.parent)
                if OUTPUT.exists() or OUTPUT.is_symlink(): raise ValueError('Fresh output required')
                started = time.monotonic()
                result = producer.write_new(OUTPUT, 'full', sys.argv[1], dev_bytes, smoke_bytes)
                if canonical(producer.source_binding()) != binding_bytes: raise ValueError('Source binding changed during execution')
                result['resource_probe'] = {'elapsed_seconds': time.monotonic() - started,
                    'peak_process_working_set_bytes': _peak_working_set(), 'scope': 'single_python_process_no_inference'}
                with (OUTPUT / 'COMPLETE.json').open('xb') as stream: stream.write(canonical(result))
                print(json.dumps(result, sort_keys=True))
            except Exception as error:
                failure_path = OUTPUT / 'failure-receipt.json'
                if OUTPUT.is_dir() and not failure_path.exists() and not (OUTPUT / 'COMPLETE.json').exists():
                    accounting = None
                    if result is not None:
                        try: accounting = _postbuild_accounting(producer, result, (OUTPUT / 'identity-metadata.json').read_bytes())
                        except (OSError, ValueError, KeyError, TypeError): pass
                    with failure_path.open('xb') as stream:
                        stream.write(canonical({'schema': 'marker-context-train-corpus-failure-v5', 'mode': 'full',
                                                'status': 'incomplete', 'error_type': type(error).__name__,
                                                'accounting_status': 'available' if accounting is not None else 'unavailable',
                                                'accounting': accounting,
                                                'training_approved': False}))
                raise
            finally:
                for stream in locks: stream.close()
        finally:
            importlib.machinery.SourceFileLoader.get_code = saved_loader


if __name__ == '__main__': main()
