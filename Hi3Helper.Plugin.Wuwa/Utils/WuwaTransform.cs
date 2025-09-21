using System;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.Wuwa.Utils;

internal class WuwaTransform(byte secret) : ICryptoTransform
{
    public bool CanReuseTransform => true;

    public bool CanTransformMultipleBlocks => true;

    public int InputBlockSize => 32;

    public int OutputBlockSize => 32;

    public void Dispose()
    {
    }

    public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
    {
        return TransformBlockCore(inputBuffer.AsSpan(inputOffset, inputCount), outputBuffer.AsSpan(outputOffset));
    }

    public int TransformBlockCore(Span<byte> inputBuffer, Span<byte> outputBuffer)
    {
        int len = inputBuffer.Length;
        int i;
        for (i = 0; i < len; i++)
        {
            byte b = inputBuffer[i];
            if (b != 10)
            {
                b ^= secret;
            }
            outputBuffer[i] = b;
        }

        return i;
    }

    public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
    {
        if (inputCount == 0)
        {
            return [];
        }
        byte[] array = new byte[inputCount];
        TransformBlock(inputBuffer, inputOffset, inputCount, array, 0);
        return array;
    }
}
