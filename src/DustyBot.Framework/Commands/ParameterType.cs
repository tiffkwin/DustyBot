﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DustyBot.Framework.Commands
{
    public enum ParameterType
    {
        Short,
        UShort,
        Int,
        UInt,
        Long,
        ULong,
        Double,
        Float,
        Decimal,
        Bool,
        String,
        Uri,
        Guid,
        Regex,

        Id,
        TextChannel,
        GuildUser,
        Role,
        GuildUserMessage,
        GuildSelfMessage
    }
}
