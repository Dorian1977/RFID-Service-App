using System;
//using System.Collections.Generic;
//using System.Linq;
using System.Runtime.InteropServices;
using BlowFishClassLibrary;
using System.Text;

namespace RFIDTag
{
    public class BlowFishImpl
    {
        static string key = "NoInkPsmCanada";

        private static byte[] writeInkData(ulong InkAvailable)
        {
            byte[] Buf = new byte[16];
            Buf[0] = Convert.ToByte('i');
            Buf[1] = Convert.ToByte('n');
            Buf[2] = Convert.ToByte('k');
            Buf[3] = Convert.ToByte('=');

            for (int i = 4; i < 12; i++)
            {
                Buf[i] = (byte)InkAvailable;
                InkAvailable /= 256;
            }

            for (int i = 12; i < 16; i++)
                Buf[i] = 0;

            return Buf;
        }

        private static ulong readInkData(byte[] Buf)
        {
            try
            {
                if ((Buf[0] != 'i') ||
                    (Buf[1] != 'n') ||
                    (Buf[2] != 'k') ||
                    (Buf[3] != '='))
                {//format not corrected
                    return 0;
                }
                ulong InkRemained = 0, temp = 0;

                for (int i = 11; i > 4; i--)
                {
                    temp = Buf[i];
                    InkRemained = (InkRemained + temp) * 256;
                }
                return InkRemained;
            }
            catch (Exception e)
            {
                return 0;
            }
        }

        public static unsafe byte[] encodeVolumeData(ulong intToneVolume)
        {
            byte[] result = new byte[16];
            byte[] BufCode = new byte[16];
            byte[] keyArr = Encoding.ASCII.GetBytes(key);
            byte[] defaultEncrypted = writeInkData(intToneVolume);

            BlowFishClassLibrary.BlowFishClass bFish = new BlowFishClass();
            fixed (byte* byteKey = &keyArr[0])
            {
                fixed (byte* defaultEncode = &defaultEncrypted[0])
                {
                    fixed (byte* bufEncode = &BufCode[0])
                    {
                        bFish.Encode(byteKey, key.Length, defaultEncode, bufEncode, 16);
                        Marshal.Copy((IntPtr)bufEncode, result, 0, result.Length);
                    }
                }
            }
            return result;
        }

        public static unsafe ulong decodeVolumeData(byte[] bytes)
        {
            byte[] result = new byte[16];
            byte[] BufCode = new byte[16];
            byte[] keyArr = Encoding.ASCII.GetBytes(key);

            BlowFishClassLibrary.BlowFishClass bFish = new BlowFishClass();
            fixed (byte* byteKey = &keyArr[0])
            {
                fixed (byte* encodeData = &bytes[0])
                {
                    fixed (byte* bufDecode = &BufCode[0])
                    {
                        bFish.Decode(byteKey, key.Length, encodeData, bufDecode, 16);
                        Marshal.Copy((IntPtr)bufDecode, result, 0, result.Length);
                    }
                }
            }
            return readInkData(result);
        }
    }
}
