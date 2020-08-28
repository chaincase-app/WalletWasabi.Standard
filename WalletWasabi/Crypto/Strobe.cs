using System;
using System.Runtime.InteropServices;
using System.Text;
using WalletWasabi.Helpers;

namespace WalletWasabi.Crypto
{
	// https://strobe.sourceforge.io/papers/strobe-20170130.pdf
	// Based on Merlin framework small implementation: https://doc-internal.dalek.rs/src/merlin/strobe.rs.html
	// https://github.com/dalek-cryptography/merlin/blob/1ed350bbc1d65f0a0697e0c20c48e11ec172c6ff/src/strobe.rs
	public sealed class Strobe128
	{
		private const byte DDATA = 0x04;
		private const byte DRATE = 0x80;

		private readonly byte[] State = new byte[25 * 8]; // this is the block size used by keccak-f1600.
		private byte _position = 0;
		private byte _beginPosition = 0;
		private StrobeFlags _currentFlags = 0;

		// Let ˆr=r/8−2. This is the portion of the rate which is used for user data, measured in bytes.
		private const byte SpongeRate = 166;

		public Strobe128(string procotol)
		{
			Guard.NotNullOrEmpty(nameof(procotol), procotol);

			var initialState = ByteHelpers.Combine(
				new byte[] { 1, (SpongeRate + 2), 1, 0, 1, 12 * 8 },  // F([[1, r/8, 1, 0, 1, 12·8]]
				Encoding.UTF8.GetBytes("STROBEv1.0.2"));
			Buffer.BlockCopy(initialState, 0, State, 0, initialState.Length);
			KeccakF1600(State);
			AddAssociatedMetaData(Encoding.UTF8.GetBytes(procotol), false);
		}

		private Strobe128(byte[] state, StrobeFlags flags, byte beginPosition, byte position)
		{
			Buffer.BlockCopy(state, 0, State, 0, State.Length);
			_currentFlags = flags;
			_beginPosition = beginPosition;
			_position = position;
		}

		~Strobe128()
		{
			Array.Clear(State, 0, State.Length);
		}

		public void AddAssociatedMetaData(byte[] data, bool more)
		{
			Guard.NotNull(nameof(data), data);

			BeginOperation(StrobeFlags.M | StrobeFlags.A, more);
			Absorb(data);
		}

		public void AddAssociatedData(byte[] data, bool more)
		{
			Guard.NotNull(nameof(data), data);

			BeginOperation(StrobeFlags.A, more);
			Absorb(data);
		}

		public byte[] Prf(uint count, bool more)
		{
			BeginOperation(StrobeFlags.I | StrobeFlags.A | StrobeFlags.C, more);
			return Squeeze(new byte[count]);
		}

		public void Key(byte[] data, bool more)
		{
			Guard.NotNull(nameof(data), data);

			BeginOperation(StrobeFlags.A | StrobeFlags.C, more);
			Override(data);
		}

		public Strobe128 MakeCopy()
		{
			return new Strobe128(State, _currentFlags, _beginPosition, _position);
		}

		internal string DumpState()
		{
			return ByteHelpers.ToHex(State);
		}

		private void Absorb(byte[] data)
		{
			foreach (var b in data)
			{
				State[_position++] ^= b;
				if (_position == SpongeRate)
				{
					RunF();
				}
			}
		}

		private void Override(byte[] data)
		{
			foreach (var b in data)
			{
				State[_position++] = b;
				if (_position == SpongeRate)
				{
					RunF();
				}
			}
		}

		private byte[] Squeeze(byte[] data)
		{
			for (var i = 0; i < data.Length; i++)
			{
				data[i] = State[_position];
				State[_position++] = 0;
				if (_position == SpongeRate)
				{
					RunF();
				}
			}
			return data;
		}

		private void BeginOperation(StrobeFlags flags, bool more)
		{
			if (more)
			{
				if (flags != _currentFlags)
				{
					throw new InvalidOperationException($"Attempt to continue operation '{_currentFlags}' with new flags '{flags}' is not allowed.");
				}
				_currentFlags = flags;
			}

			// Skip adjusting direction information (we just use AD, PRF)
			if (_currentFlags.HasFlag(StrobeFlags.T))
			{
				throw new NotImplementedException("Transport operations are not implemented.");
			}

			var oldBeginPosition = _beginPosition;
			_currentFlags = flags;
			_beginPosition = (byte)(_position + 1);

			Absorb(new[] { oldBeginPosition, (byte)flags });

			// Force running F if C or K is set
			var forceF = flags.HasFlag(StrobeFlags.C) || flags.HasFlag(StrobeFlags.K);

			if (forceF && _position != 0)
			{
				RunF();
			}
		}

		// This is the Sponge function responsible for shuffling the internal state.
		private void RunF()
		{
			State[_position] ^= _beginPosition;
			State[_position + 1] ^= DDATA;
			State[SpongeRate + 1] ^= DRATE;

			KeccakF1600(State);

			_position = 0;
			_beginPosition = 0;
		}

		// Taken from Bouncy Castle project.
		// https://github.com/bcgit/bc-csharp/blob/1cbd476c1eaf32a928a926ebb0ec346821753661/crypto/src/crypto/digests/KeccakDigest.cs
		// License as MIT https://www.bouncycastle.org/licence.html
		private static readonly ulong[] KeccakRoundConstants = new ulong[]
		{
			0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
			0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
			0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
			0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
			0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
			0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
		};

		private static void KeccakF1600(byte[] state)
		{
			Span<ulong> buffer = MemoryMarshal.Cast<byte, ulong>(state);

			ulong a00 = buffer[00], a01 = buffer[01], a02 = buffer[02], a03 = buffer[03], a04 = buffer[04];
			ulong a05 = buffer[05], a06 = buffer[06], a07 = buffer[07], a08 = buffer[08], a09 = buffer[09];
			ulong a10 = buffer[10], a11 = buffer[11], a12 = buffer[12], a13 = buffer[13], a14 = buffer[14];
			ulong a15 = buffer[15], a16 = buffer[16], a17 = buffer[17], a18 = buffer[18], a19 = buffer[19];
			ulong a20 = buffer[20], a21 = buffer[21], a22 = buffer[22], a23 = buffer[23], a24 = buffer[24];

			for (int i = 0; i < 24; i++)
			{
				// theta
				ulong c0 = a00 ^ a05 ^ a10 ^ a15 ^ a20;
				ulong c1 = a01 ^ a06 ^ a11 ^ a16 ^ a21;
				ulong c2 = a02 ^ a07 ^ a12 ^ a17 ^ a22;
				ulong c3 = a03 ^ a08 ^ a13 ^ a18 ^ a23;
				ulong c4 = a04 ^ a09 ^ a14 ^ a19 ^ a24;

				ulong d1 = (c1 << 1 | c1 >> -1) ^ c4;
				ulong d2 = (c2 << 1 | c2 >> -1) ^ c0;
				ulong d3 = (c3 << 1 | c3 >> -1) ^ c1;
				ulong d4 = (c4 << 1 | c4 >> -1) ^ c2;
				ulong d0 = (c0 << 1 | c0 >> -1) ^ c3;

				a00 ^= d1; a05 ^= d1; a10 ^= d1; a15 ^= d1; a20 ^= d1;
				a01 ^= d2; a06 ^= d2; a11 ^= d2; a16 ^= d2; a21 ^= d2;
				a02 ^= d3; a07 ^= d3; a12 ^= d3; a17 ^= d3; a22 ^= d3;
				a03 ^= d4; a08 ^= d4; a13 ^= d4; a18 ^= d4; a23 ^= d4;
				a04 ^= d0; a09 ^= d0; a14 ^= d0; a19 ^= d0; a24 ^= d0;

				// rho/pi
				c1 = a01 << 01 | a01 >> 63;
				a01 = a06 << 44 | a06 >> 20;
				a06 = a09 << 20 | a09 >> 44;
				a09 = a22 << 61 | a22 >> 03;
				a22 = a14 << 39 | a14 >> 25;
				a14 = a20 << 18 | a20 >> 46;
				a20 = a02 << 62 | a02 >> 02;
				a02 = a12 << 43 | a12 >> 21;
				a12 = a13 << 25 | a13 >> 39;
				a13 = a19 << 08 | a19 >> 56;
				a19 = a23 << 56 | a23 >> 08;
				a23 = a15 << 41 | a15 >> 23;
				a15 = a04 << 27 | a04 >> 37;
				a04 = a24 << 14 | a24 >> 50;
				a24 = a21 << 02 | a21 >> 62;
				a21 = a08 << 55 | a08 >> 09;
				a08 = a16 << 45 | a16 >> 19;
				a16 = a05 << 36 | a05 >> 28;
				a05 = a03 << 28 | a03 >> 36;
				a03 = a18 << 21 | a18 >> 43;
				a18 = a17 << 15 | a17 >> 49;
				a17 = a11 << 10 | a11 >> 54;
				a11 = a07 << 06 | a07 >> 58;
				a07 = a10 << 03 | a10 >> 61;
				a10 = c1;

				// chi
				c0 = a00 ^ (~a01 & a02);
				c1 = a01 ^ (~a02 & a03);
				a02 ^= ~a03 & a04;
				a03 ^= ~a04 & a00;
				a04 ^= ~a00 & a01;
				a00 = c0;
				a01 = c1;

				c0 = a05 ^ (~a06 & a07);
				c1 = a06 ^ (~a07 & a08);
				a07 ^= ~a08 & a09;
				a08 ^= ~a09 & a05;
				a09 ^= ~a05 & a06;
				a05 = c0;
				a06 = c1;

				c0 = a10 ^ (~a11 & a12);
				c1 = a11 ^ (~a12 & a13);
				a12 ^= ~a13 & a14;
				a13 ^= ~a14 & a10;
				a14 ^= ~a10 & a11;
				a10 = c0;
				a11 = c1;

				c0 = a15 ^ (~a16 & a17);
				c1 = a16 ^ (~a17 & a18);
				a17 ^= ~a18 & a19;
				a18 ^= ~a19 & a15;
				a19 ^= ~a15 & a16;
				a15 = c0;
				a16 = c1;

				c0 = a20 ^ (~a21 & a22);
				c1 = a21 ^ (~a22 & a23);
				a22 ^= ~a23 & a24;
				a23 ^= ~a24 & a20;
				a24 ^= ~a20 & a21;
				a20 = c0;
				a21 = c1;

				// iota
				a00 ^= KeccakRoundConstants[i];
			}

			buffer[00] = a00; buffer[01] = a01; buffer[02] = a02; buffer[03] = a03; buffer[04] = a04;
			buffer[05] = a05; buffer[06] = a06; buffer[07] = a07; buffer[08] = a08; buffer[09] = a09;
			buffer[10] = a10; buffer[11] = a11; buffer[12] = a12; buffer[13] = a13; buffer[14] = a14;
			buffer[15] = a15; buffer[16] = a16; buffer[17] = a17; buffer[18] = a18; buffer[19] = a19;
			buffer[20] = a20; buffer[21] = a21; buffer[22] = a22; buffer[23] = a23; buffer[24] = a24;
		}

		/// <summary>
		/// The behavior of each of Strobe's operations is defined completely by 6 features, called flags.
		/// </summary>
		[Flags]
		private enum StrobeFlags : byte
		{
			/// <summary>
			/// Inbound.
			/// If set, this flag means that the operation moves data from the transport, to the cipher,
			/// to the application. An operation without the I flag set is said to be Outbound.
			/// The I flag is clear on all send operations, and set on all recv operations.
			/// </summary>
			I = 1,

			/// <summary>
			/// Application.
			/// If set, this flag means that the operation has data coming to or from the application side.
			/// An operation with I and A both set outputs bytes to the application.
			/// An operation with A set but I clear takes input from the application.
			/// </summary>
			A = 2,

			/// <summary>
			/// Cipher.
			/// If set, this flag means that the operation's output depends cryptographically on the Strobe cipher state.
			/// For operations which don't have I or T flags set, neither party produces output with this operation.
			/// In that case, the C flag instead means that the operation acts as a rekey or ratchet.
			/// </summary>
			C = 4,

			/// <summary>
			/// Transport.
			/// If set, this flag means that the operation sends or receives data using the transport.
			/// An operation has T set if and only if it has send or recv in its name.
			/// An operation with I and T both set receives data from the transport.
			/// An operation with T set but I clear sends data to the transport.
			/// </summary>
			T = 8,

			/// <summary>
			/// Meta.
			/// If set, this flag means that the operation is handling framing, transcript comments or some other sort of protocol metadata.
			/// It doesn't affect how the operation is performed.
			/// </summary>
			M = 16,

			/// <summary>
			/// Keytree.
			/// This flag is reserved for a certain protocol-level countermeasure against side-channel analysis.
			/// It does affect how an operation is performed.
			/// This specification does not describe its use. For all operations in this specification, the K flag must be clear.
			/// </summary>
			K = 32
		}
	}
}
