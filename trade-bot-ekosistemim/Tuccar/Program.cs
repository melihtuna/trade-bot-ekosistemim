using Binance.API.Csharp.Client;
using Binance.API.Csharp.Client.Models.Enums;
using Binance.API.Csharp.Client.Models.Market;
using Binance.API.Csharp.Client.Utils;
using Microsoft.Practices.EnterpriseLibrary.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Mail;
using System.Text;
using Trady.Analysis.Indicator;
using Trady.Core;
using Trady.Core.Infrastructure;

namespace Tuccar
{
    class Program
    {
        private static string mail_Adresleri = "Rapor maili almak istediğiniz mail adresini giriniz";

        public static List<string> HataLogList = new List<string>();

        private static readonly DateTime StartUnixTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static void Main(string[] args)
        {
            Console.BufferHeight = Int16.MaxValue - 1;
            var baslangic = DateTime.Now;

            var binanceClient = new BinanceClient(
                new ApiClient(
                    "apiKey",
                    "apiSecret"
                ));

            Islemci(binanceClient);

            //mail_Adresleri ve MailGonderAsync alanlarındaki mail alanlarını doldurduktan sonra bu kısmı aktif ediniz.
            //Raporla(baslangic, DateTime.Now);
        }

        private static void Islemci(BinanceClient binanceClient)
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var coms = db.GetSqlStringCommand("select * from IslemTablosu where IslemKapandiMi=0");
                DataSet _islemTablosuDataSet = db.ExecuteDataSet(coms);

                //sembolleri grupla ve tickerPrices çek
                if (_islemTablosuDataSet.Tables.Count > 0 && _islemTablosuDataSet.Tables[0].Rows.Count > 0)
                {
                    var acikSemboller = _islemTablosuDataSet.Tables[0].AsEnumerable()
                        .GroupBy(r => r.Field<string>("Sembol"))
                        .Select(g => g.First().Field<string>("Sembol"))
                        .ToArray();

                    var tickerPrices = binanceClient.GetAllPrices().Result
                        .Where(x => acikSemboller.Contains(x.Symbol))
                        .Select(i => new SymbolPrice { Symbol = i.Symbol, Price = i.Price })
                        .OrderBy(x => x.Symbol).ToList();

                    //döngüyle, sembollerin mostlarını hesapla, son değerleri ve farkları logla, sat durumuna geçenleri kapanan işlemler olarak işaretle, db güncelle
                    foreach (DataRow islemRow in _islemTablosuDataSet.Tables[0].Rows)
                    {
                        var coin = tickerPrices.Where(x => x.Symbol == islemRow["Sembol"].ToString()).ToList().First();
                        var coinAdeti = Convert.ToDecimal(islemRow["CoinAdeti"].ToString());
                        var kontrolPariteKarsiligi = coin.Price * coinAdeti;
                        var karPariteKarsiligi = kontrolPariteKarsiligi - Convert.ToDecimal(islemRow["GirisPariteKarsiligi"].ToString());
                        var karOran = karPariteKarsiligi / Convert.ToDecimal(islemRow["GirisPariteKarsiligi"].ToString()) * 100;

                        var pikOrani = Convert.ToDecimal(islemRow["PikOrani"].ToString());
                        var pikFiyat = Convert.ToDecimal(islemRow["PikFiyat"].ToString());
                        var pikTarihi = Convert.ToDateTime(islemRow["PikTarihi"].ToString());

                        if (karOran > pikOrani)
                        {
                            pikOrani = karOran;
                            pikFiyat = coin.Price;
                            pikTarihi = DateTime.Now;
                        }

                        var islemDurum = 0;

                        // kar oranı -2 ise stoplaması için değer islemDurum=1 yapıyor. kendi zarar tahammül oranınıza göre değişebilirsiniz.
                        if (karOran < -2)
                        {
                            islemDurum = 1;
                        }
                        else
                        {
                            var periyot = ZamanEnumDon(islemRow["Periyot"].ToString());
                            var ema = Convert.ToInt32(islemRow["MostParametreleri"].ToString().Replace("(", "").Replace(")", "").Split(',')[0]);
                            var yuzde = Convert.ToDouble(islemRow["MostParametreleri"].ToString().Replace("(", "").Replace(")", "").Split(',')[1]) / 100;

                            var candlestick = binanceClient.GetCandleSticks(coin.Symbol, periyot, limit: 500).Result;
                            var candlesticks = candlestick as Candlestick[] ?? candlestick.Where(x => StartUnixTime.AddMilliseconds(x.CloseTime).ToUniversalTime() < DateTime.Now.ToUniversalTime()).ToArray();

                            List<IOhlcv> tradyCandles = candlesticks.Select(candle => new Candle(StartUnixTime.AddMilliseconds(candle.OpenTime).ToUniversalTime(), candle.Open, candle.High, candle.Low, candle.Close, candle.Volume)).Cast<IOhlcv>().ToList();

                            var mostListe = MostDLL.MostHesapla.Hesapla(tradyCandles, ema, yuzde);
                            var iftsListe = InverseFisherTransformOnStochastic.IftsHesapla.Hesapla(tradyCandles, 5, 9);
                            var macdListe = MacdHesapla(tradyCandles);

                            List<IslemListe> islemListe = StratejiSonucHesapla(coin, periyot, ema, yuzde, mostListe, iftsListe, macdListe);
                            
                            if (Convert.ToDateTime(islemRow["GirisBarAcilisTarihi"].ToString()).AddMinutes(PeriyottanDakikaDon(islemRow["Periyot"].ToString())).ToUniversalTime() < DateTime.Now.ToUniversalTime())
                            {
                                islemDurum = islemListe.Last().Durum == "AL" ? 0 : 1;
                            }
                        }

                        //if (islemDurum == 1)
                        //{
                        //      Borsadaki satış işlemini burada yapabilirsiniz
                        //      Doküman:
                        //      https://github.com/melihtuna/Binance.API.Csharp.Client/blob/master/Documentation/AccountMethods.md
                        //}

                        var comm = db.GetSqlStringCommand("UPDATE IslemTablosu SET KontrolTarihi=@KontrolTarihi, KontrolFiyat=@KontrolFiyat, KontrolPariteKarsiligi=@KontrolPariteKarsiligi, KarPariteKarsiligi=@KarPariteKarsiligi, KarOrani=@KarOrani, PikFiyat=@PikFiyat, PikOrani=@PikOrani, PikTarihi=@PikTarihi, IslemKapandiMi=@IslemKapandiMi WHERE ID=@ID");
                        db.AddInParameter(comm, "KontrolTarihi", DbType.DateTime, DateTime.Now);
                        db.AddInParameter(comm, "KontrolFiyat", DbType.Decimal, Convert.ToDecimal(coin.Price));
                        db.AddInParameter(comm, "KontrolPariteKarsiligi", DbType.Decimal, kontrolPariteKarsiligi);
                        db.AddInParameter(comm, "KarPariteKarsiligi", DbType.Decimal, karPariteKarsiligi);
                        db.AddInParameter(comm, "KarOrani", DbType.Decimal, karOran);
                        db.AddInParameter(comm, "PikFiyat", DbType.Decimal, pikFiyat);
                        db.AddInParameter(comm, "PikOrani", DbType.Decimal, pikOrani);
                        db.AddInParameter(comm, "PikTarihi", DbType.DateTime, pikTarihi);
                        db.AddInParameter(comm, "IslemKapandiMi", DbType.Int32, islemDurum);
                        db.AddInParameter(comm, "ID", DbType.Int32, Convert.ToInt32(islemRow["ID"].ToString()));
                        db.ExecuteNonQuery(comm);
                    }
                }
            }
            catch (Exception e)
            {
                HataLogList.Add("Islemci Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        private static List<decimal> MacdHesapla(List<IOhlcv> tradyCandles)
        {
            if (tradyCandles.Count > 360)
            {
                var mcdList = new MovingAverageConvergenceDivergenceHistogram(tradyCandles, 12, 26, 9);
                var hesapla = mcdList.Compute();

                var sonuc = hesapla.Select(x => Convert.ToDecimal(x.Tick)).ToList();
                sonuc.RemoveRange(0, 140);
                return sonuc;
            }

            return null;
        }

        private static List<IslemListe> StratejiSonucHesapla(SymbolPrice coin, TimeInterval periyot, int emaUzunluk, double yuzde, List<MostDLL.Most> mostListe, List<double> iftsListe, List<decimal> macdListe)
        {
            List<IslemListe> islemListe = new List<IslemListe>();
            var sgIslemSay = 0;
            var sgPozisyon = "SAT";

            for (int i = 4; i < mostListe.Count; i++)
            {
                var itemMost = mostListe[i];
                if (itemMost.MostDurum == "AL")
                {
                    if (sgPozisyon == "SAT")
                    {
                        var iftsOnay = iftsListe[i - 1] >= -0.5 && iftsListe[i - 2] < -0.5 && iftsListe[i - 3] < -0.5;
                        var macdOnay = macdListe[i - 1] > 0;

                        if (iftsOnay && macdOnay)
                        {
                            sgIslemSay++;
                            sgPozisyon = "AL";
                        }
                    }
                }
                else
                {
                    if (sgPozisyon == "AL")
                    {
                        sgPozisyon = "SAT";
                    }
                }

                DateTime utc = itemMost.Bar.DateTime.UtcDateTime;
                islemListe.Add(new IslemListe
                {
                    Sembol = coin.Symbol,
                    MostParametreleri = "(" + emaUzunluk + "," + yuzde * 100 + ")",
                    Periyot = periyot.GetDescription(),
                    AcilisZamani = utc.ToLocalTime(),
                    Durum = sgPozisyon,
                    EmaDeger = itemMost.EmaDegeri,
                    Fiyat = itemMost.Bar.Open,
                    MostDeger = itemMost.MostDegeri,
                    IslemSayisi = sgIslemSay,
                    Bar = itemMost.Bar
                });
            }

            return islemListe;
        }

        private static TimeInterval ZamanEnumDon(string periyot)
        {
            switch (periyot)
            {
                case "1m":
                    return TimeInterval.Minutes_1;
                case "3m":
                    return TimeInterval.Minutes_3;
                case "5m":
                    return TimeInterval.Minutes_5;
                case "15m":
                    return TimeInterval.Minutes_15;
                case "30m":
                    return TimeInterval.Minutes_30;
                case "1h":
                    return TimeInterval.Hours_1;
                case "2h":
                    return TimeInterval.Hours_2;
                case "4h":
                    return TimeInterval.Hours_4;
                case "6h":
                    return TimeInterval.Hours_6;
                case "8h":
                    return TimeInterval.Hours_8;
                case "12h":
                    return TimeInterval.Hours_12;
                case "1d":
                    return TimeInterval.Days_1;
                case "3d":
                    return TimeInterval.Days_3;
                case "1w":
                    return TimeInterval.Weeks_1;
                case "1M":
                    return TimeInterval.Months_1;
            }

            return TimeInterval.Days_1;
        }

        private static int PeriyottanDakikaDon(string periyot)
        {
            switch (periyot)
            {
                case "1m":
                    return 1;
                case "3m":
                    return 3;
                case "5m":
                    return 5;
                case "15m":
                    return 15;
                case "30m":
                    return 30;
                case "1h":
                    return 60;
                case "2h":
                    return 120;
                case "4h":
                    return 240;
                case "6h":
                    return 360;
                case "8h":
                    return 480;
                case "12h":
                    return 720;
                case "1d":
                    return 1440;
                case "3d":
                    return 4320;
                case "1w":
                    return 10080;
                case "1M":
                    return 43200;
            }

            return 1440;
        }

        private static void Raporla(DateTime baslangic, DateTime bitis)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<p>Başlangıç: " + baslangic + " | Bitiş: " + bitis + " | İşlem Süresi: " +
                        $"{(bitis - baslangic).TotalMinutes:N0}" + " dakika</p>");

            DatabaseProviderFactory factory = new DatabaseProviderFactory();
            Database db = factory.Create("connStrBot");

            //kapanan pozisyonlar
            var comk = db.GetSqlStringCommand("SELECT * FROM IslemTablosu WHERE IslemKapandiMi=1 and KontrolTarihi>DATEADD(MINUTE,-4,GETDATE()) ORDER BY Sembol");
            var kapananPozisyonlarDataSet = db.ExecuteDataSet(comk);
            if (kapananPozisyonlarDataSet.Tables.Count > 0 && kapananPozisyonlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<h3>KAPANAN POZİSYONLAR (" + kapananPozisyonlarDataSet.Tables[0].Rows.Count + " ADET) : </h3>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append(RaporTabloBasliklar());

                foreach (DataRow dr in kapananPozisyonlarDataSet.Tables[0].Rows)
                {
                    sb.Append(RaporTabloSatir(dr));
                }

                sb.Append("</table>");
            }

            //tüm açık pozisyonlar
            var comt = db.GetSqlStringCommand("SELECT * FROM IslemTablosu WHERE IslemKapandiMi=0 ORDER BY Sembol");
            var tumAcikPozisyonlarDataSet = db.ExecuteDataSet(comt);
            if (tumAcikPozisyonlarDataSet.Tables.Count > 0 && tumAcikPozisyonlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<br/><h3>TÜM AÇIK POZİSYONLAR (" + tumAcikPozisyonlarDataSet.Tables[0].Rows.Count + " ADET) : </h3>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append(RaporTabloBasliklar());
                foreach (DataRow dr in tumAcikPozisyonlarDataSet.Tables[0].Rows)
                {
                    sb.Append(RaporTabloSatir(dr));
                }

                sb.Append("</table>");

                IslemDetayKaydet(tumAcikPozisyonlarDataSet.Tables[0]);
            }

            //kar zarar rapor 
            var comr = db.GetSqlStringCommand(@"select (select AVG(KarOrani) from IslemTablosu where IslemKapandiMi = 1) KapananIslemKarOran, 
            (select COUNT(*) from IslemTablosu where IslemKapandiMi = 1) KapananIslemSayisi, 
            (select AVG(KarOrani) from IslemTablosu where IslemKapandiMi = 0) AcikIslemKarOran, 
            (select COUNT(*) from IslemTablosu where IslemKapandiMi = 0) AcikIslemSayisi,
            AVG(KarOrani) TumIslemlerKarOran,
            COUNT(*) TumIslemlerinSayisi
            from IslemTablosu");
            var genelToplamlarDataSet = db.ExecuteDataSet(comr);
            if (genelToplamlarDataSet.Tables.Count > 0 && genelToplamlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<br/><h3>GENEL DURUM</h3><br/>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append("<tr>");
                sb.Append("<td>Kapanan İşlem Kar Oranı</td>");
                sb.Append("<td>Kapanan İşlem Sayısı</td>");
                sb.Append("<td>Açık İşlem Kar Oranı</td>");
                sb.Append("<td>Açık İşlem Sayısı</td>");
                sb.Append("<td>Tüm İşlemler Kar Oranı</td>");
                sb.Append("<td>Tüm İşlemlerin Sayısı</td>");
                sb.Append("</tr>");

                sb.Append("<tr>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["KapananIslemKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["KapananIslemSayisi"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["AcikIslemKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["AcikIslemSayisi"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["TumIslemlerKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["TumIslemlerinSayisi"] + "</td>");
                sb.Append("</tr>");

                sb.Append("</table>");
            }

            string mailListesi = mail_Adresleri;
            string[] alicilar = mailListesi.Split(',');
            List<string> liste = alicilar.ToList();

            MailGonderAsync(liste, "Tüccar Robotu - " + baslangic.ToString("dd-MM-yyyy HH:mm"), sb.ToString(), System.Net.Mail.MailPriority.High);
        }

        public static void IslemDetayKaydet(DataTable dtIslemler)
        {
            try
            {
                var tarih = DateTime.Now;
                for (int i = 0; i < dtIslemler.Rows.Count; i++)
                {
                    DatabaseProviderFactory factory = new DatabaseProviderFactory();
                    Database db = factory.Create("connStrBot");
                    var comm = db.GetSqlStringCommand("INSERT INTO FiyatKontrolTablosu (IslemID,KontrolTarihi,KontrolFiyat,KarOrani) VALUES (@IslemID,@KontrolTarihi,@KontrolFiyat,@KarOrani)");
                    db.AddInParameter(comm, "IslemID", DbType.Int32, dtIslemler.Rows[i]["ID"]);
                    db.AddInParameter(comm, "KontrolTarihi", DbType.DateTime, tarih);
                    db.AddInParameter(comm, "KontrolFiyat", DbType.Decimal, dtIslemler.Rows[i]["KontrolFiyat"]);
                    db.AddInParameter(comm, "KarOrani", DbType.Decimal, dtIslemler.Rows[i]["KarOrani"]);

                    db.ExecuteNonQuery(comm);
                }
            }
            catch (Exception e)
            {
                HataLogList.Add("IslemDetayKaydet Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        public static string RaporTabloBasliklar()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<tr>");
            sb.Append("<td>Sembol</td>");
            sb.Append("<td>Periyot</td>");
            sb.Append("<td>Most</td>");
            sb.Append("<td>Giriş Tarihi</td>");
            sb.Append("<td>Giriş Fiyatı</td>");
            sb.Append("<td>Kontrol Tarihi</td>");
            sb.Append("<td>Kontrol Fiyatı</td>");
            sb.Append("<td>Kar Oranı (%)</td>");
            sb.Append("</tr>");
            return sb.ToString();
        }

        public static string RaporTabloSatir(DataRow dr)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<tr>");
            sb.Append("<td>" + dr["Sembol"] + "</td>");
            sb.Append("<td>" + dr["Periyot"] + "</td>");
            sb.Append("<td>Most" + dr["MostParametreleri"] + "</td>");
            sb.Append("<td>" + Convert.ToDateTime(dr["GirisTarihi"]).ToString("dd-MM-yyyy HH:mm:ss") + "</td>");
            sb.Append("<td>" + dr["GirisFiyat"] + "</td>");
            sb.Append("<td>" + Convert.ToDateTime(dr["KontrolTarihi"]).ToString("dd-MM-yyyy HH:mm:ss") + "</td>");
            sb.Append("<td>" + dr["KontrolFiyat"] + "</td>");
            sb.Append("<td>" + dr["KarOrani"] + "</td>");
            sb.Append("</tr>");
            return sb.ToString();
        }

        public static void MailGonderAsync(List<string> aliciMailAdresleri, string epostaKonusu, string epostaIcerigi, MailPriority oncelikSeviyesi)
        {
            try
            {
                int port = 587;
                string sunucu = "smtp.gmail.com";
                string sifre = "Gmail Şifreniz";
                string kullaniciAd = "Gmail Adresiniz";
                bool sslAktifMi = true;

                SmtpClient smtpClient = new SmtpClient(sunucu, port)
                {
                    Credentials = new System.Net.NetworkCredential(kullaniciAd, sifre),
                    EnableSsl = sslAktifMi
                };

                MailMessage mm = new MailMessage
                {
                    From = new MailAddress(kullaniciAd, epostaKonusu, Encoding.UTF8),
                    Priority = oncelikSeviyesi
                };

                foreach (var alici in aliciMailAdresleri)
                    mm.To.Add(alici);
                mm.SubjectEncoding = Encoding.GetEncoding("UTF-8");
                mm.BodyEncoding = Encoding.GetEncoding("UTF-8");
                mm.Subject = epostaKonusu;
                mm.Body = epostaIcerigi;
                mm.IsBodyHtml = true;
                smtpClient.Send(mm);

                Console.WriteLine("==================== BİTTİ ====================");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Hata" + ex);
            }
        }
    }

    public class IslemListe
    {
        public int IslemIndex { get; set; }
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public DateTime AcilisZamani { get; set; }
        public decimal MostDeger { get; set; }
        public decimal EmaDeger { get; set; }
        public string Durum { get; set; }
        public decimal Fiyat { get; set; }
        public int IslemSayisi { get; set; }
        public IOhlcv Bar { get; set; }
    }

    public class RehberTablo
    {
        public SymbolPrice Sembol { get; set; }
        public TimeInterval Periyot { get; set; }
        public KeyValuePair<int, int> MostParametreleri { get; set; }
    }
}
