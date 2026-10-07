import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const targets = fileURLToPath(new URL('../../Directory.Build.targets', import.meta.url));
const escape = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const rids = ['win-x64', 'win-arm64', 'win', 'linux-x64', 'linux-musl-arm64', 'linux-arm64', 'linux', 'unix-arm64', 'unix', 'osx-x64', 'any'];

function probe(properties = {}, stale = false) {
    const directory = mkdtempSync(join(tmpdir(), 'tuvima-runtime-test-'));
    try {
        if (stale) {
            const output = stale === 'publish' ? 'published' : 'output';
            mkdirSync(join(directory, output, 'runtimes', 'osx-x64', 'native'), { recursive: true });
            writeFileSync(join(directory, output, 'runtimes', 'osx-x64', 'native', 'foreign.dylib'), 'fixture');
        }
        const project = `<Project>
<PropertyGroup><NETCoreSdkRuntimeIdentifier>win-x64</NETCoreSdkRuntimeIdentifier>
<RuntimeIdentifierGraphPath>$(MSBuildToolsPath)/PortableRuntimeIdentifierGraph.json</RuntimeIdentifierGraphPath>
<TargetDir>${escape(resolve(directory, 'output'))}/</TargetDir>
<PublishDir>${escape(resolve(directory, 'published'))}/</PublishDir>
${Object.entries(properties).map(([key, value]) => `<${key}>${escape(value)}</${key}>`).join('')}</PropertyGroup>
<Import Project="${escape(targets)}"/>
<Target Name="ResolvePackageAssets"><ItemGroup>
${rids.map(rid => `<RuntimeTargetsCopyLocalItems Include="${rid}"><RuntimeIdentifier>${rid}</RuntimeIdentifier></RuntimeTargetsCopyLocalItems>`).join('')}
<NativeCopyLocalItems Include="unscoped.dll"/>
<NativeCopyLocalItems Include="runtimes/linux-x64/native/fallback.so"/>
</ItemGroup></Target>
<Target Name="ResolveLockFileCopyLocalFiles" DependsOnTargets="ResolvePackageAssets">
<WriteLinesToFile File="${escape(join(directory, 'build.txt'))}" Lines="@(RuntimeTargetsCopyLocalItems);@(NativeCopyLocalItems)" Overwrite="true"/>
</Target>
<Target Name="ComputeResolvedFilesToPublish"><ItemGroup>
${rids.map(rid => `<ResolvedFileToPublish Include="publish-${rid}"><RelativePath>runtimes/${rid}/native/library</RelativePath></ResolvedFileToPublish>`).join('')}
<ResolvedFileToPublish Include="managed.dll"/>
</ItemGroup></Target>
<Target Name="Build" DependsOnTargets="ResolveLockFileCopyLocalFiles;ComputeResolvedFilesToPublish">
<WriteLinesToFile File="${escape(join(directory, 'publish.txt'))}" Lines="@(ResolvedFileToPublish)" Overwrite="true"/>
</Target><Target Name="Publish" DependsOnTargets="Build"/></Project>`;
        writeFileSync(join(directory, 'test.proj'), project);
        const result = spawnSync('dotnet', ['msbuild', join(directory, 'test.proj'), '-t:Publish', '-nologo', '-v:minimal'], { encoding: 'utf8', timeout: 60000 });
        const lines = name => readFileSync(join(directory, name), 'utf8').trim().split(/\r?\n/).sort();
        return { ...result, build: result.status === 0 ? lines('build.txt') : [], publish: result.status === 0 ? lines('publish.txt') : [] };
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}

test('host build and publish keep Windows fallbacks and unscoped assets', () => {
    const result = probe();
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(result.build, ['any', 'unscoped.dll', 'win', 'win-x64'].sort());
    assert.deepEqual(result.publish, ['managed.dll', 'publish-any', 'publish-win', 'publish-win-x64'].sort());
});

test('explicit musl ARM64 target overrides Windows host and follows RID imports', () => {
    const result = probe({ RuntimeIdentifier: 'linux-musl-arm64' });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(result.build, ['any', 'linux', 'linux-arm64', 'linux-musl-arm64', 'unix', 'unix-arm64', 'unscoped.dll'].sort());
});

test('explicit Linux x64 preserves native path fallback', () => {
    const result = probe({ RuntimeIdentifier: 'linux-x64' });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.ok(result.build.includes('runtimes/linux-x64/native/fallback.so'));
    assert.ok(!result.build.includes('win-x64'));
});

test('portable opt-in preserves the complete catalog', () => {
    const result = probe({ TuvimaKeepAllRuntimeAssets: 'true' }, true);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.equal(result.build.length, rids.length + 2);
    assert.equal(result.publish.length, rids.length + 1);
});

test('unknown RID fails clearly instead of silently stripping libraries', () => {
    const result = probe({ RuntimeIdentifier: 'unknown-runtime' });
    assert.notEqual(result.status, 0);
    assert.match(result.stdout, /Unknown runtime/);
});

test('stale foreign output fails the build with cleanup instructions', () => {
    const result = probe({}, true);
    assert.notEqual(result.status, 0);
    assert.match(result.stdout, /Clean-RepoOutputs\.ps1/);
});

test('stale foreign publish files fail even when build output is clean', () => {
    const result = probe({}, 'publish');
    assert.notEqual(result.status, 0);
    assert.match(result.stdout, /obsolete generated publish directory/);
});
