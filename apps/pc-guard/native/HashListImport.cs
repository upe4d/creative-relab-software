using System;using System.Collections.Generic;using System.IO;using System.Text.RegularExpressions;
namespace PCGuard {
    // Только текстовые SHA-256. Образцы malware и исполняемый код не загружаются.
    internal static class HashListImport {
        internal static Dictionary<string,object>[] Parse(string text,string fileName) {
            var rows=new List<Dictionary<string,object>>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern=new Regex(@"(?<![A-Za-z0-9])[a-fA-F0-9]{64}(?![A-Za-z0-9])",RegexOptions.CultureInvariant);
            using(var reader=new StringReader(text)){string line;int lines=0;while((line=reader.ReadLine())!=null){
                if(++lines>250000||line.Length>65536)throw new InvalidDataException("Превышен предел строк или длина строки списка SHA-256.");
                if(line.TrimStart().StartsWith("#")||String.IsNullOrWhiteSpace(line))continue;
                foreach(Match match in pattern.Matches(line)){string hash=match.Value.ToLowerInvariant();if(!seen.Add(hash))continue;
                    if(rows.Count>=100000)throw new InvalidDataException("В импортируемом списке больше 100000 SHA-256.");
                    rows.Add(Store.Map("sha256",hash,"name","Imported SHA-256 indicator","action","alert","source","Локальный список: "+fileName));
                }
            }}
            if(rows.Count==0)throw new InvalidDataException("В файле не найдены отдельные SHA-256. MD5, SHA-1, URL и CVD не являются совместимой базой PC Guard.");
            return rows.ToArray();
        }
    }
}
