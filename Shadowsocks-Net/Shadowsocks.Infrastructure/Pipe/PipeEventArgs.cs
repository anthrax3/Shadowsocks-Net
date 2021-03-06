﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace Shadowsocks.Infrastructure.Pipe
{
    public class PipeEventArgs :EventArgs
    {
        public IPipe Pipe { set; get; }        
        public PipeException Exception { set; get; }
    }
}
