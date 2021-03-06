﻿using Newtonsoft.Json;
using Platform_Racing_3_Server.Game.Communication.Messages.Incoming.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace Platform_Racing_3_Server.Game.Communication.Messages.Outgoing.Json
{
    internal class JsonRoomVarsOutgoingMessage : JsonPacket
    {
        internal override string Type => "receiveRoomVars";

        [JsonProperty("roomName")]
        internal string RoomName { get; set; }

        [JsonProperty("vars")]
        internal IReadOnlyDictionary<string, object> Vars { get; set; }

        internal JsonRoomVarsOutgoingMessage(string roomName, IReadOnlyDictionary<string, object> vars)
        {
            this.RoomName = roomName;
            this.Vars = vars;
        }
    }
}
