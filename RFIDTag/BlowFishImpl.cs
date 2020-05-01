using System;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace RFIDTag
{
    public class BlowFishImpl
    {
        static string key = "NoInkPsmCanada";

        private static byte[] writeInkData(ulong InkAvailable, UInt32 dongleID)
        {
            byte[] Buf = new byte[16];
            Buf[0] = Convert.ToByte('i');
            Buf[1] = Convert.ToByte('n');
            Buf[2] = Convert.ToByte('k');
            Buf[3] = Convert.ToByte('=');

            for (int i = 4; i < 12; i++)
            {
                Buf[i] = (byte)InkAvailable;
                InkAvailable >>= 8;
            }

            for (int i = 12; i < 16; i++)
            {
                Buf[i] = (byte)dongleID;
                dongleID >>= 8;
            }
            return Buf;
        }

        private static ulong readInkData(byte[] Buf, out UInt32 dongleID)
        {
            UInt32 _dongleID = 0;
            dongleID = 0;
            //return 2000000000;
            try
            {
                if ((Buf[0] != 'i') ||
                    (Buf[1] != 'n') ||
                    (Buf[2] != 'k') ||
                    (Buf[3] != '='))
                {//format not corrected
                    dongleID = 0;
                    return 0;
                }

                ulong InkRemained = 0, temp = 0;

                for (int i = 11; i >= 4; i--)
                {
                    //temp = Buf[i];
                    //InkRemained = (InkRemained + temp) * 256;
                    InkRemained <<= 8;
                    InkRemained |= (ulong)Buf[i];
                }
                for (int i = 15; i >= 12; i--)
                {
                    //temp = BufResult[i];
                    //DongleId = (DongleId + temp) * 256;
                    _dongleID <<= 8;
                    _dongleID |= (UInt32)Buf[i];
                }
                dongleID = _dongleID;
                return InkRemained;
            }
            catch (Exception e)
            {
                dongleID = 0;
                return 0;
            }
        }
#if USE_DLL
        //[DllImport(@"C:\\mycompany\\MyDLL.dll")]
        //C:\ProgramData\RFIDTag\BlowFish Release
        //[DllImport(@"C:\\ProgramData\\RFIDTag\\BlowFish Release\\BlowFishClassLibrary.dll", EntryPoint = "Encode")]
        //[DllImport("..\\..\\..\\..\\Debug\\BlowFishDLL.dll", EntryPoint = "_Encode")]
        [DllImport("BlowFishClassLibrary.dll", 
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode, EntryPoint = "Encode")]
        private static extern unsafe UInt32 Encode(byte* key, int keybytes, byte* pInput,
                                            byte* pOutput, UInt32 lSize);

        //[DllImport("..\\..\\..\\..\\Debug\\BlowFishDLL.dll", EntryPoint = "_Decode")]
        [DllImport("BlowFishClassLibrary.dll",
                CallingConvention = CallingConvention.StdCall,
                CharSet = CharSet.Unicode, EntryPoint = "Decode")]
        //[DllImport(@"C:\\ProgramData\\RFIDTag\\BlowFish Release\\BlowFishClassLibrary.dll", EntryPoint = "Decode")]
        private static extern unsafe void Decode(byte* key, int keybytes,
                                    byte* pInput, byte* pOutput, UInt32 lSize);
#endif
#if true
        public static byte[] encodeVolumeData(ulong intToneVolume, UInt32 dongleID)
        {
            byte[] keyArr = Encoding.ASCII.GetBytes(key);
            byte[] defaultEncrypted = writeInkData(intToneVolume, dongleID);

            Blowfish blowFish = new Blowfish(keyArr);
            byte[] result = new byte[16];
            result = blowFish.Encipher(defaultEncrypted, defaultEncrypted.Length);
            return result;
        }
#else

        public static unsafe byte[] encodeVolumeData(ulong intToneVolume, UInt32 dongleID)
        {
#if !USE_BLOW_FISH
            // byte[] result = { 6,125,22,94,21,241,27,119,
            //                   64,253,228,114,115,97,206,149};
#endif
            byte[] result = new byte[16];
            byte[] BufCode = new byte[16];
            byte[] keyArr = Encoding.ASCII.GetBytes(key);
            byte[] defaultEncrypted = writeInkData(intToneVolume, dongleID);
#if USE_DLL
            fixed (byte* byteKey = &keyArr[0])
            {
                fixed (byte* defaultEncode = &defaultEncrypted[0])
                {
                    fixed (byte* bufEncode = &BufCode[0])
                    {
                        Encode(byteKey, key.Length, defaultEncode, bufEncode, 16);
                        Marshal.Copy((IntPtr)bufEncode, result, 0, result.Length);
                    }
                }
            }
#else

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

#endif

            return result;
        }
#endif
#if true
        public static ulong decodeVolumeData(byte[] bytes, out UInt32 dongleID)
        {
            byte[] keyArr = Encoding.ASCII.GetBytes(key);
            Blowfish blowFish = new Blowfish(keyArr);
            byte[] decode = blowFish.Decipher(bytes, bytes.Length);
            return readInkData(decode, out dongleID);
        }
#else
        public static unsafe ulong decodeVolumeData(byte[] bytes, out UInt32 dongleID)
        {
#if !USE_BLOW_FISH
            byte[] result = { 6,125,22,94,21,241,27,119,
                              64,253,228,114,115,97,206,149};
#endif
            byte[] result = new byte[16];
            byte[] BufCode = new byte[16];
            byte[] keyArr = Encoding.ASCII.GetBytes(key);
#if USE_DLL
            fixed (byte* byteKey = &keyArr[0])
            {
                fixed (byte* encodeData = &bytes[0])
                {
                    fixed (byte* bufDecode = &BufCode[0])
                    {
                        Decode(byteKey, key.Length, encodeData, bufDecode, 16);
                        Marshal.Copy((IntPtr)bufDecode, result, 0, result.Length);
                    }
                }
            }
#else
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
#endif
            return readInkData(result, out dongleID);

        }
#endif
    }
}