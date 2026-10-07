import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, existsSync, rmSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const cleaner = fileURLToPath(new URL('../../tools/Clean-RepoOutputs.ps1', import.meta.url));

function fixture(action) {
    const directory = mkdtempSync(join(tmpdir(), 'tuvima-clean-test-'));
    const repo = join(directory, 'repo');
    const put = (path, contents = 'fixture') => {
        mkdirSync(join(repo, path, '..'), { recursive: true });
        writeFileSync(join(repo, path), contents);
    };
    const git = (...args) => {
        const result = spawnSync('git', ['-C', repo, ...args], { encoding: 'utf8' });
        assert.equal(result.status, 0, result.stdout + result.stderr);
    };
    try {
        put('MediaEngine.slnx');
        put('app/Test.csproj');
        mkdirSync(join(repo, 'tools'), { recursive: true });
        copyFileSync(cleaner, join(repo, 'tools', 'Clean-RepoOutputs.ps1'));
        git('init', '-q');
        git('add', 'MediaEngine.slnx', 'app/Test.csproj', 'tools/Clean-RepoOutputs.ps1');
        const run = (...args) => spawnSync('pwsh', ['-NoProfile', '-File', join(repo, 'tools', 'Clean-RepoOutputs.ps1'), ...args], { encoding: 'utf8', timeout: 60000 });
        action({ directory, repo, put, git, run });
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}

test('cleanup removes generated outputs and preserves source, data, and temporary notes', () => fixture(({ repo, put, run }) => {
    put('app/bin/Debug/generated.dll');
    put('app/obj/project.assets.json');
    put('.tmp/mud/generated.dll');
    put('.tmp/notes.patch');
    put('.tmp/docs-venv/keep');
    put('.data/library.db');
    const result = run('-IncludeQa');
    assert.equal(result.status, 0, result.stdout + result.stderr);
    for (const path of ['app/bin', 'app/obj', '.tmp/mud']) assert.ok(!existsSync(join(repo, path)), path);
    for (const path of ['app/Test.csproj', '.tmp/notes.patch', '.tmp/docs-venv/keep', '.data/library.db']) assert.ok(existsSync(join(repo, path)), path);
}));

test('WhatIf validates the plan without deleting outputs', () => fixture(({ repo, put, run }) => {
    put('app/bin/Debug/generated.dll');
    const result = run('-WhatIf');
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.ok(existsSync(join(repo, 'app/bin/Debug/generated.dll')));
}));

test('QA-only cleanup preserves current project builds', () => fixture(({ repo, put, run }) => {
    put('app/bin/Debug/generated.dll');
    put('.tmp/storage-publish/generated.dll');
    const result = run('-QaOnly');
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.ok(existsSync(join(repo, 'app/bin/Debug/generated.dll')));
    assert.ok(!existsSync(join(repo, '.tmp/storage-publish')));
}));

test('tracked output aborts the complete plan before any deletion', () => fixture(({ repo, put, git, run }) => {
    put('app/bin/generated.dll');
    put('app/obj/protected.txt');
    git('add', 'app/obj/protected.txt');
    const result = run();
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /tracked files/);
    assert.ok(existsSync(join(repo, 'app/bin/generated.dll')));
}));

test('external junction or symlink is refused and its target survives', () => fixture(({ directory, repo, run }) => {
    const outside = join(directory, 'outside');
    mkdirSync(outside);
    writeFileSync(join(outside, 'keep.txt'), 'preserve');
    symlinkSync(outside, join(repo, 'app/bin'), process.platform === 'win32' ? 'junction' : 'dir');
    const result = run();
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Refusing linked/);
    assert.ok(existsSync(join(outside, 'keep.txt')));
}));
