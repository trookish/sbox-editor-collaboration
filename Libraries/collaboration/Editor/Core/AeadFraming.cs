using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TeamCreate;

public sealed class AeadFraming
{
    public const int KeyBytes = 32;

    public const int NonceBytes = 12;

    public const int TagBytes = 16;

    public const int FrameOverhead = 28;

    private static readonly byte[] InitiatorTag = new byte[4] { 67, 84, 45, 65 };

    private static readonly byte[] ResponderTag = new byte[4] { 67, 84, 45, 66 };

    private readonly byte[] key;

    private readonly byte[] sealTag;

    private readonly byte[] openTag;

    private readonly object gate = new object();

    private long counter = 1L;

    // The counter the next opened frame must carry. Sealing numbers frames 1, 2, 3 without gaps,
    // and a stream (TCP, or the relay's ordered byte pipe) delivers them in that order, so anything
    // else on the wire is a replayed, dropped, duplicated or reordered frame. Accepting any valid
    // tag was fine while only the two endpoints could touch the stream; a relay sits in the middle
    // and could otherwise re-inject or withhold frames without breaking a single authentication tag.
    private long expectedOpenCounter = 1L;

    private long bytesSealed;

    public const string SequenceFailure = "A sealed frame arrived out of sequence; the stream was replayed, reordered or had frames removed.";

    public long BytesSealed => bytesSealed;

    public AeadFraming(byte[] key, bool initiator = false)
    {
        if (key == null || key.Length != 32)
        {
            throw new ArgumentOutOfRangeException("key", "An AEAD session key must be exactly 32 bytes.");
        }
        this.key = (byte[])key.Clone();
        sealTag = (initiator ? InitiatorTag : ResponderTag);
        openTag = (initiator ? ResponderTag : InitiatorTag);
    }

    internal void SetCounterForTests(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException("value", "The framing counter starts at 1 and never runs backwards.");
        }
        lock (gate)
        {
            counter = value;
        }
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        long value;
        lock (gate)
        {
            if (counter >= long.MaxValue)
            {
                throw new InvalidOperationException("The framing counter is exhausted; reconnect with a fresh session key instead of reusing a nonce.");
            }
            value = counter++;
            bytesSealed += plaintext.Length;
        }
        byte[] array = new byte[28 + plaintext.Length];
        Span<byte> span = array.AsSpan(0, 12);
        sealTag.CopyTo(span);
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(4), value);
        using AesGcm aesGcm = new AesGcm(key, 16);
        aesGcm.Encrypt(span, plaintext, array.AsSpan(12, plaintext.Length), array.AsSpan(12 + plaintext.Length, 16));
        return array;
    }

    public byte[] Open(ReadOnlySpan<byte> sealedFrame)
    {
        if (sealedFrame.Length < 28)
        {
            throw new CryptographicException("The sealed frame is too short to hold a nonce and an authentication tag.");
        }
        ReadOnlySpan<byte> nonce = sealedFrame.Slice(0, 12);
        if (!nonce.Slice(0, 4).SequenceEqual(openTag))
        {
            throw new CryptographicException("The sealed frame carries the opposite direction tag; both ends of a session must use opposite initiator flags.");
        }
        long counterOnWire = BinaryPrimitives.ReadInt64BigEndian(nonce.Slice(4));
        lock (gate)
        {
            if (counterOnWire != expectedOpenCounter)
            {
                throw new CryptographicException(SequenceFailure);
            }
        }
        byte[] array = new byte[sealedFrame.Length - 28];
        using AesGcm aesGcm = new AesGcm(key, 16);
        aesGcm.Decrypt(nonce, sealedFrame.Slice(12, array.Length), sealedFrame.Slice(12 + array.Length), array);
        // Advance only after the tag verified: a forged frame must not be able to move the window.
        lock (gate)
        {
            expectedOpenCounter = counterOnWire + 1;
        }
        return array;
    }
}
