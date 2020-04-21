using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RFIDTag
{
    public class tagInfo
    {
        public static string label = "";
        public static string tagDecryptedInfo = "";
        public static string tagReserve = "";
        public static string tagData = "";
        public static bool bVerified = false;
    }
        
    public class RFIDTagInfo
    {
        private const ulong tonerMAXVolume = 2000000000000; //max is 2L * 1000000000
        private static ulong currentTonerVolume = 0; //1L = 1,000,000,000,000 pL

        private static string labelFormat = "";
        private static string tonerVolumeFile = "";
        private static string inkAuthFile = "";       
        public const char serialSep = '=';        
        public static List<string> labelList = new List<string>();
        
        public static void loadVolumeFilePath(string filePath)
        {
            tonerVolumeFile = filePath;
        }

        public static void loadinkAuthFilePath(string filePath)
        {
            inkAuthFile = filePath;
        }

        public static void loadLabelFormat(byte[] lableFormatBytes)
        {
            labelFormat = Encoding.ASCII.GetString(lableFormatBytes);
        }

        public static string readLabelFormat()
        {
            return labelFormat;
        }
       
        static bool writeByteToFile(byte[] encodeData)
        {
            try
            {
                using (FileStream fileStream =
                    new FileStream(tonerVolumeFile, 
                        FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    // Write the data to the file, byte by byte.
                    for (int i = 0; i < encodeData.Length; i++)
                    {
                        fileStream.WriteByte(encodeData[i]);
                    }
                    fileStream.Close();
                }
    }
            catch(Exception e)
            {
                Thread.Sleep(500);
                return false;
            }
            return true;
        }

        public static bool readInkAuthFile(out string encryptedData)
        {//1. check file
         //2. check encrypt data
         //3. verify timeStamp
         //4. update volume
            encryptedData = "";
            try
            {
                if (File.Exists(inkAuthFile))
                {
                    FileStream logFileStream = 
                        new FileStream(inkAuthFile,
                           FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    using (StreamReader sr = new StreamReader(logFileStream))
                    {
                        while (!sr.EndOfStream)
                        {
                            encryptedData = sr.ReadLine();                            
                        }
                        sr.Close();
                        logFileStream.Close();
                    }
                    if (encryptedData == null || encryptedData == "")
                    {
                        encryptedData = "";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception exp) { }
            return false;
        }

        public static void addTempInkAuth()
        {           
            try
            {
                File.Delete(inkAuthFile);
                addVolumeToFile(1000);
            }
            catch(Exception exp) { }
        }

        protected static bool IsFileLocked(FileInfo file)
        {
            try
            {
                using (FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch (IOException)
            {
                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist (has already been processed)
                return true;
            }

            //file is not locked
            return false;
        }

        public static bool addVolumeToFile(int intToneVolume)
        {
            bool bWritedFile = false;
            try
            {//1. check file existed
             //2. read data inside
             //3. if read data is empty, write volume read from tag
             //4. if read data not empty, add the volume read from tag, if over max, set to max
                /*if(IsFileLocked(new FileInfo(tonerVolumeFile)))
                { // APrint file locked the ink data in following:
                  //  1. start print
                  //  2. stop print
                  //  3. Done                    
                    return false;
                }*/

                byte[] defaultData = BlowFishImpl.encodeVolumeData((ulong)intToneVolume * 1000000000);
                if (!File.Exists(tonerVolumeFile))
                {
                    bWritedFile = false;
                    for(int i=0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(defaultData);
                        
                        if (bWritedFile)
                        {
                            Console.WriteLine("Ink file not found, write " +
                                Encoding.Default.GetString(defaultData));
                            return true;
                        }
                    }
                    return false;
                }

                //read only
                var readData = default(byte[]);          
                FileStream logFileStream = new FileStream(tonerVolumeFile, 
                                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using (StreamReader sr = new StreamReader(logFileStream))
                {
                    using (var memstream = new MemoryStream())
                    {
                        sr.BaseStream.CopyTo(memstream);
                        readData = memstream.ToArray();
                    }
                    // Clean up
                    sr.Close();
                    logFileStream.Close();
                }

                if (readData.Length == 0)
                {//empty file
                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(defaultData);

                        if (bWritedFile)
                        {
                            Console.WriteLine("Ink file empty, write " +
                                Encoding.Default.GetString(defaultData));
                            return true;
                        }
                    }
                }
                else
                {
                    ulong tmpVolume = BlowFishImpl.decodeVolumeData(readData);
                    currentTonerVolume = tmpVolume + (ulong)intToneVolume * 1000000000;
                    Console.Write("Ink file read " + tmpVolume.ToString());

                    if (currentTonerVolume > tonerMAXVolume)
                        currentTonerVolume = tonerMAXVolume;

                    bWritedFile = false;
                    for (int i = 0; i < 5 && !bWritedFile; i++)
                    {
                        bWritedFile = writeByteToFile(BlowFishImpl.encodeVolumeData(currentTonerVolume));

                        if (bWritedFile)
                        {
                            Console.WriteLine(", Ink file is update, write " +
                                Encoding.Default.GetString(BlowFishImpl.encodeVolumeData(currentTonerVolume)));
                            return true;
                        }
                    }
                }
            }
            catch(Exception exp)
            {
            }
            return false;
        }

        public static void addLog(string path)
        {
            if (tagInfo.label == "" || tagInfo.tagDecryptedInfo == "")
                return;

            string[] tmpList = tagInfo.tagDecryptedInfo.Split(serialSep);
            string strHeadType = tmpList[1].Substring(0, 2);
            string strIntType = tmpList[1].Substring(2, 2);
            string strVolume = tmpList[1].Substring(4, 4);
            string strDate = tmpList[1].Substring(8, 4);
            string strSupplier = tmpList[1].Substring(12, 2);

            if(!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            if(File.Exists(path + "\\log.dat"))
            {
                File.Decrypt(path + "\\log.dat");
            }
            try
            {
                using (StreamWriter sw = File.AppendText(path + "\\log.dat"))
                {
                    DateTime localDate = DateTime.Now;
                    sw.Write("{0},", localDate.ToString("MM/dd/yy HH:mm:ss"));
                    sw.Write(tagInfo.label + "," + strHeadType + "," + strIntType + ",");
                    sw.WriteLine(strVolume + "," + strDate + "," + strSupplier);
                    File.Encrypt(path + "\\log.dat");
                }
            }
            catch (Exception exp) { }
        }

        public static bool checkAuthDateAndLog(string input)
        {
            //check serialNumber and dateTime
            string[] dataArry = input.Split(serialSep);
            DateTime parsedDate = DateTime.Now;
            try
            {
                parsedDate = DateTime.ParseExact(dataArry[0], "MMddyy", CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return false;
            }

            DateTime ExpireDay = DateTime.Now.AddDays(1);
            string strExpireDate = ExpireDay.ToString("MMddyy");

            if (ExpireDay < parsedDate)
                return false;
                        
            //string serialNumber = dataArry[1];
            string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (File.Exists(path + "\\log.dat"))
            {
                using (StreamReader sr = File.OpenText(path + "\\log.dat"))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] strArry = line.Split(',');
                        if (strArry[1] == dataArry[1])
                        {//tag has been use before
                            return false;
                        }
                    }
                }
                return true;
            }
            return false;
        }

        public static bool verifyData(string inputData, bool bVerifyData, bool bRead2Erase)
        {//1. input label = correct, bRead2Erase = false,
         //2. input label = 0, bRead2Erase = true;
            if (inputData == "")
                return false;
            try
            {
                if(bRead2Erase)
                {
                    ulong uData = 0;
                    if(ulong.TryParse(inputData.Replace(" ", ""), out uData))
                    {
                        if (uData == 0)
                            return true;
                    }
                    return false;                       
                }
                if (bVerifyData)
                {
                    string[] checkData = inputData.Split(RFIDTagInfo.serialSep);
                    if (checkData[0].StartsWith("PS"))
                    {
                        return true;
                    }
                    return false;
                }
                else
                {
                    for (int i = 0; i < labelFormat.Length; i++)
                    {
                        if (inputData[i] != labelFormat[i])
                        {
                            if (labelFormat[i] == '_')
                            {
                                continue;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            catch(Exception exp)
            {
                Console.WriteLine("Read tag " + inputData + " got exception " + exp.Message);
                return false;
            }              
            return false;
        }

        public static string ASCIIToHex(string input)
        {
            //return String.Concat(input.Select(x => ((int)x).ToString("x")));
            StringBuilder sb = new StringBuilder();
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);

            foreach (byte b in inputBytes)
            {
                sb.Append(string.Format("{0:x2} ", b));
            }
            return sb.ToString();
        }

        public static string readEPCLabel(string strEPC)
        {
            try
            {
                string EPClabel = RFIDTagInfo.HEXToASCII(strEPC.Substring(0, 6).ToUpper());
                string serialNumber = strEPC.Substring(7).Replace(" ", "");
                ulong uEPCNumber = ulong.Parse(serialNumber);
                string EPSnumber = uEPCNumber.ToString("D20");
                return EPClabel + EPSnumber; ;
            }
            catch(Exception e)
            {
                return "";
            }            
        }

        public static string HEXToASCII(string input)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < input.Length; i += 3)
            {
                var hexChar = input.Substring(i, 3).Trim();
                sb.Append((char)Convert.ToByte(hexChar, 16));
            }
            return sb.ToString();
        }

        public static string AddHexSpace(string input)
        {
            return String.Join(" ", Regex.Matches(input, @"\d{2}")
                        .OfType<Match>()
                        .Select(m => m.Value).ToArray());
        }

        static string BytesToString(byte[] bytes)
        {
            using (MemoryStream st = new MemoryStream(bytes))
            {
                using (StreamReader sr = new StreamReader(st))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        public static Byte[] GetBytesFromBinaryString(String input)
        {
            var list = new List<Byte>();
            var binaryList = input.Split(' ');

            for (int i = 0; i < binaryList.Length; i++)
            {
                list.Add(Convert.ToByte(binaryList[i], 2));
            }
            return list.ToArray();
        }

        public static Byte[] GetPassword(string strValue, int nLength)
        {
            string[] reslut = CCommondMethod.StringToStringArray(strValue, nLength);
            return CCommondMethod.StringArrayToByteArray(reslut, reslut.Length);
        }

        public static Byte[] GetData(string strValue, int nLength)
        {
            string[] reslut = CCommondMethod.StringToStringArray(strValue.ToUpper(), nLength);
            return CCommondMethod.StringArrayToByteArray(reslut, reslut.Length);
        }
    }
}
