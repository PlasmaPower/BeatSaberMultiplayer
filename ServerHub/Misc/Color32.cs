﻿using System;
using System.Collections.Generic;
using System.Text;

namespace ServerHub.Misc
{
    [Serializable]
    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;

        public static Color32 defaultColor = new Color32(255, 255, 255);
        public static Color32 roomHostColor = new Color32(255, 0, 0);

        public Color32(byte r, byte g, byte b)
        {
            this.r = r;
            this.g = g;
            this.b = b;
        }

        public override bool Equals(object obj)
        {
            if (obj is Color32)
            {
                return ((Color32)obj).r == r && ((Color32)obj).g == g && ((Color32)obj).b == b;
            }
            else
                return false;
        }

        public override int GetHashCode()
        {
            var hashCode = -839137856;
            hashCode = hashCode * -1521134295 + r.GetHashCode();
            hashCode = hashCode * -1521134295 + g.GetHashCode();
            hashCode = hashCode * -1521134295 + b.GetHashCode();
            return hashCode;
        }
    }
}
