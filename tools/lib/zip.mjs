// A small zip writer and reader (Node standard library only) so the Roku channel package can be built on any machine
// without a zip program. Entries are deflated, named with forward slashes, and stamped with a fixed date so the same
// files always give the same bytes.
import { deflateRawSync, inflateRawSync } from 'node:zlib';

const crcTable = new Int32Array(256).map((_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c;
});
const crc32 = (buffer) => {
    let c = -1;
    for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ -1) >>> 0;
};

const DOS_TIME = 0; // 00:00:00
const DOS_DATE = (1 << 5) | 1; // 1980-01-01

/** entries: [{ name: 'source/main.brs', data: Buffer }] in the order they should appear. */
export const createZip = (entries) => {
    const parts = [];
    const central = [];
    let offset = 0;
    for (const { name, data } of entries) {
        const nameBytes = Buffer.from(name, 'utf8');
        const packed = deflateRawSync(data, { level: 9 });
        const crc = crc32(data);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4); // version needed
        local.writeUInt16LE(0x0800, 6); // names are UTF-8
        local.writeUInt16LE(8, 8); // deflate
        local.writeUInt16LE(DOS_TIME, 10);
        local.writeUInt16LE(DOS_DATE, 12);
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(packed.length, 18);
        local.writeUInt32LE(data.length, 22);
        local.writeUInt16LE(nameBytes.length, 26);
        parts.push(local, nameBytes, packed);

        const entry = Buffer.alloc(46);
        entry.writeUInt32LE(0x02014b50, 0);
        entry.writeUInt16LE(20, 4); // version made by
        entry.writeUInt16LE(20, 6); // version needed
        entry.writeUInt16LE(0x0800, 8);
        entry.writeUInt16LE(8, 10);
        entry.writeUInt16LE(DOS_TIME, 12);
        entry.writeUInt16LE(DOS_DATE, 14);
        entry.writeUInt32LE(crc, 16);
        entry.writeUInt32LE(packed.length, 20);
        entry.writeUInt32LE(data.length, 24);
        entry.writeUInt16LE(nameBytes.length, 28);
        entry.writeUInt32LE(offset, 42);
        central.push(entry, nameBytes);
        offset += local.length + nameBytes.length + packed.length;
    }
    const centralBytes = Buffer.concat(central);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(entries.length, 8);
    end.writeUInt16LE(entries.length, 10);
    end.writeUInt32LE(centralBytes.length, 12);
    end.writeUInt32LE(offset, 16);
    return Buffer.concat([...parts, centralBytes, end]);
};

/** Reads a zip made by createZip (or any zip with deflate or stored entries) back into [{ name, data }]. */
export const readZip = (buffer) => {
    const endAt = buffer.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
    if (endAt < 0) throw new Error('Not a zip file.');
    const count = buffer.readUInt16LE(endAt + 10);
    let at = buffer.readUInt32LE(endAt + 16);
    const result = [];
    for (let i = 0; i < count; i++) {
        if (buffer.readUInt32LE(at) !== 0x02014b50) throw new Error('Bad zip directory.');
        const method = buffer.readUInt16LE(at + 10);
        const crc = buffer.readUInt32LE(at + 16);
        const packedSize = buffer.readUInt32LE(at + 20);
        const nameLength = buffer.readUInt16LE(at + 28);
        const extraLength = buffer.readUInt16LE(at + 30);
        const commentLength = buffer.readUInt16LE(at + 32);
        const localAt = buffer.readUInt32LE(at + 42);
        const name = buffer.toString('utf8', at + 46, at + 46 + nameLength);
        const dataAt = localAt + 30 + buffer.readUInt16LE(localAt + 26) + buffer.readUInt16LE(localAt + 28);
        const packed = buffer.subarray(dataAt, dataAt + packedSize);
        const data = method === 8 ? inflateRawSync(packed) : Buffer.from(packed);
        if (crc32(data) !== crc) throw new Error(`Checksum mismatch for ${name}.`);
        result.push({ name, data });
        at += 46 + nameLength + extraLength + commentLength;
    }
    return result;
};
