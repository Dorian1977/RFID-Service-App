using System;
using System.Collections.Generic;
//using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RFIDTag
{
    class Symmetric_Encrypted
    {
        decryptedState decState = decryptedState.CombinedMemory;
        enum decryptedState
        {
            CombinedMemory,
            SingleMemroy
        }

        private static AesManaged aes;        
        //public byte[] encryptedtext;
        private string accessCode = "";
 
        public Symmetric_Encrypted()
        {
            aes = new AesManaged();
        }

        public void resetEncrypted()
        {
            decState = decryptedState.CombinedMemory;
        }

        public void loadAccessCode(string code)
        {
            accessCode = code;
        }

        public string readAccessCode()
        {
            return accessCode;
        }

        public void loadKey(byte [] keyValue)
        {
            aes.Key = keyValue;
        }
        /* 
        public string readKey()
        {
            return Convert.ToBase64String(aes.Key);
        }*/

        public string Encrypt(string plainText)//, byte[] Key)
        {
            byte[] encrypted;
            byte[] iv = new byte[16];
            try
            {
                if(aes.Key == null || plainText == "")
                {
                    return null;
                }
                // Create a new AesManaged.    
                using (AesManaged aesTemp = new AesManaged())
                {
                    ICryptoTransform encryptor = aesTemp.CreateEncryptor(aes.Key, iv);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        // Create crypto stream using the CryptoStream class. This class is the key to encryption    
                        // and encrypts and decrypts data from any given stream. In this case, we will pass a memory stream    
                        // to encrypt    
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            // Create StreamWriter and write data to a stream    
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(plainText);
                            }
                            encrypted = ms.ToArray();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //Trace.WriteLine(e.Message);
                return null;
            }
            // Return encrypted data    
            return Convert.ToBase64String(encrypted);
        }

        public bool IsBase64String(string s)
        {
            s = s.Trim();
            return (s.Length % 4 == 0) && Regex.IsMatch(s, @"^[a-zA-Z0-9\+/]*={0,3}$", RegexOptions.None);
        }

        public string DecryptData(byte[] cipherText)
        {
            string plaintext = "";
            byte[] iv = new byte[16];
            //byte[] cipherText = new byte[32];

            if (cipherText.Length == 0)
                return "";

            try
            {
               
                using (AesManaged aesTemp = new AesManaged())
                {
                    // Create a decryptor
                    aesTemp.IV = iv;
                    ICryptoTransform decryptor = aesTemp.CreateDecryptor(aes.Key, aesTemp.IV);
                    // Create the streams used for decryption.    
                    using (MemoryStream ms = new MemoryStream(cipherText))
                    {
                        // Create crypto stream    
                        using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        {
                            // Read crypto stream    
                            using (StreamReader reader = new StreamReader(cs))
                                plaintext = reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception exp) { /*Trace.WriteLine("Get Exception " + exp.Message);*/ }
            return plaintext;
        }

        public string DecryptFromHEX(string inputPart1, string inputPart2)
        {
            // Create AesManaged
            if (aes.Key == null || inputPart2 == "")
            {//can't covert 0x00 to ASCII word
                return "";
            }

            for (int j = 0; j < inputPart2.Length / 6; j++)
            {
                int pos = inputPart2.LastIndexOf("00");
                if (pos != -1)
                {
                    inputPart2 = inputPart2.Remove(pos - 3);
                }
                else
                {
                    break;
                }
            }

            for (int i = 0; i < 2; i++)
            {
                string hex2Ascii = "";
                string plaintext = "";
                try
                {
                    switch(decState)
                    {
                        case decryptedState.CombinedMemory:
                            {
                                hex2Ascii = RFIDTagInfo.HEXToASCII(
                                                    inputPart1 + inputPart2);
                                
                                if (!IsBase64String(hex2Ascii))
                                {
                                    //Trace.WriteLine("Format not support1 " + hex2Ascii);
                                    decState = decryptedState.SingleMemroy;
                                    continue;
                                }                                                          
                            }
                            break;
                        case decryptedState.SingleMemroy:
                            {
                                hex2Ascii = RFIDTagInfo.HEXToASCII(inputPart2);                                    
                                if (!IsBase64String(hex2Ascii))
                                {
                                    //Trace.WriteLine("Format not support2 " + inputPart2);
                                    decState = decryptedState.CombinedMemory;
                                    return "";
                                }                               
                            }
                            break;
                    }


                    //Trace.WriteLine("Read encrypted message " + hex2Ascii +
                    //                  ", decState = " + decState.ToString());
                    byte[] cipherText = System.Convert.FromBase64String(hex2Ascii);
                    plaintext = DecryptData(cipherText);

                    if (plaintext != "")
                    {
                        decState = decryptedState.CombinedMemory;
                        return plaintext;
                    }
                }
                catch (Exception exp)  {   }
                switch (decState)
                {
                    case decryptedState.CombinedMemory:
                        decState = decryptedState.SingleMemroy;
                        break;
                    case decryptedState.SingleMemroy:
                        decState = decryptedState.CombinedMemory;
                        return "";
                }
            }
            return "";
        }

    }
}
