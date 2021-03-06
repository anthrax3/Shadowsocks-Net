﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Collections.Generic;
using System.Text;
using Argument.Check;

namespace Shadowsocks.Cipher
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class CipherAttribute : Attribute
    {
        public string Name { set; get; }

        public CipherAttribute(string name)
        {
            if (string.IsNullOrEmpty(name)) { throw new ArgumentNullException("name"); }
            Name = name;
        }

    }
}
