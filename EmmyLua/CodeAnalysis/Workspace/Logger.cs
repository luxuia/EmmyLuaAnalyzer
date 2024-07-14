using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace EmmyLua.CodeAnalysis.Workspace;

public class Logger {

    public static StreamWriter writer;

    public static int writeLineCount = 0;

    public class Log :IDisposable {

        string prefix;
        long startTime;

        public string extra = string.Empty;

        public Log(string prefix) {
        
            this.prefix = prefix;
            startTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }


        public void Dispose() {
            
            Logger.writer.WriteLine($"{prefix} CostTime {(DateTimeOffset.Now.ToUnixTimeMilliseconds() - startTime)/1000.0} {extra}");

            writeLineCount++;

            if (writeLineCount > 100) {
                Logger.writer.Flush();

                writeLineCount = 0;
            }
        }
    }
}

