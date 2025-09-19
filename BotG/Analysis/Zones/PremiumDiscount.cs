namespace Analysis.Zones
{
    public static class PremiumDiscount
    {
        public struct Range { public double Low; public double High; }
        public static bool InDiscount(double price, Range r) => price <= (r.Low + 0.5*(r.High - r.Low));
        public static bool InPremium(double price, Range r)  => price >  (r.Low + 0.5*(r.High - r.Low));
    }
}