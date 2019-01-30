﻿using FSO.Common.DataService;
using FSO.Common.DataService.Model;
using FSO.Common.Domain.Realestate;
using FSO.Common.Domain.RealestateDomain;
using FSO.Common.Enum;
using FSO.Server.Common;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Lots;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.Electron.Packets;
using Ninject;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FSO.Server.Servers.City.Handlers
{
    public class PurchaseLotHandler
    {
        private IRealestateDomain GlobalRealestate;
        private IShardRealestateDomain Realestate;
        private IDAFactory DA;
        private IDataService DataService;
        private CityServerContext Context;
        private IKernel Kernel;
        
        public PurchaseLotHandler(CityServerContext context, IRealestateDomain realestate, IDAFactory da, IDataService dataService, IKernel kernel)
        {
            Context = context;
            GlobalRealestate = realestate;
            Realestate = realestate.GetByShard(context.ShardId);
            DA = da;
            DataService = dataService;
            Kernel = kernel;
        }

        public async void Handle(IVoltronSession session, PurchaseLotRequest packet)
        {
            if (session.IsAnonymous) //CAS users can't do this.
                return;

            var isPurchasable = Realestate.IsPurchasable(packet.LotLocation_X, packet.LotLocation_Y);

            if (!isPurchasable){
                session.Write(new PurchaseLotResponse(){
                    Status = PurchaseLotStatus.FAILED,
                    Reason = PurchaseLotFailureReason.LOT_NOT_PURCHASABLE
                });
                return;
            }

            var packedLocation = MapCoordinates.Pack(packet.LotLocation_X, packet.LotLocation_Y);
            var price = Realestate.GetPurchasePrice(packet.LotLocation_X, packet.LotLocation_Y);
            int resultFunds;

            uint lotId = 0;

            using (var db = DA.Get())
            {
                if (db.Lots.GetByLocation(Context.ShardId, packedLocation) != null)
                {
                    session.Write(new PurchaseLotResponse()
                    {
                        Status = PurchaseLotStatus.FAILED,
                        Reason = PurchaseLotFailureReason.LOT_TAKEN,
                    });
                    return;
                }


                var ownedLot = db.Lots.GetByOwner(session.AvatarId);
                if (ownedLot != null)
                {
                    //we own the lot we are roomie of.
                    var roommates = db.Roommates.GetLotRoommates(ownedLot.lot_id);
                    var ds = await DataService.Get<FSO.Common.DataService.Model.Lot>(ownedLot.location);

                    if (ds.Lot_IsOnline)
                    {
                        session.Write(new PurchaseLotResponse()
                        {
                            Status = PurchaseLotStatus.FAILED,
                            Reason = PurchaseLotFailureReason.NOT_OFFLINE_FOR_MOVE //TODO: race condition might make this possible?
                        });
                        return;
                    }

                    if (roommates.Count > 1)
                    {
                        //cannot start fresh with roommates for now.
                        packet.StartFresh = false;
                    }

                    var oldLoc = MapCoordinates.Unpack(ownedLot.location);
                    var moveCost = price - Realestate.GetPurchasePrice(oldLoc.X, oldLoc.Y);
                    moveCost += 2000; //flat rate for moving location

                    var transactionResult = db.Avatars.Transaction(session.AvatarId, uint.MaxValue, moveCost, 5); //expenses misc... maybe add specific for lot
                    resultFunds = transactionResult.source_budget;
                    if (!transactionResult.success)
                    {
                        session.Write(new PurchaseLotResponse()
                        {
                            Status = PurchaseLotStatus.FAILED,
                            Reason = PurchaseLotFailureReason.INSUFFICIENT_FUNDS,
                            NewFunds = resultFunds
                        });
                        return;
                    }

                    if (!db.Lots.UpdateLocation(ownedLot.lot_id, packedLocation, packet.StartFresh))
                    {
                        //needs to refund the player.
                        var transactionResult2 = db.Avatars.Transaction(uint.MaxValue, session.AvatarId, moveCost, 5); 
                        session.Write(new PurchaseLotResponse()
                        {
                            Status = PurchaseLotStatus.FAILED,
                            Reason = PurchaseLotFailureReason.LOT_TAKEN,
                            NewFunds = transactionResult2.source_budget
                        });
                        return;
                    }

                    DataService.Invalidate<FSO.Common.DataService.Model.Lot>(ownedLot.location); //nullify old lot
                    DataService.Invalidate<FSO.Common.DataService.Model.Lot>(packedLocation); //update new lot
                }
                else
                {
                    //we may still be roomie in a lot. If we are, we must be removed from that lot.
                    var myLots = db.Roommates.GetAvatarsLots(session.AvatarId);
                    if (myLots.Count > 0)
                    {
                        if (myLots[0].permissions_level > 1)
                        {
                            //owner should not be able to move out of a lot implicitly
                            session.Write(new PurchaseLotResponse()
                            {
                                Status = PurchaseLotStatus.FAILED,
                                Reason = PurchaseLotFailureReason.UNKNOWN
                            });
                            return;
                        }
                        var lot = db.Lots.Get(myLots[0].lot_id);
                        if (lot != null)
                        {
                            var kickResult = await Kernel.Get<ChangeRoommateHandler>().TryKick(lot.location, session.AvatarId, session.AvatarId);
                            if (kickResult != Protocol.Electron.Model.ChangeRoommateResponseStatus.SELFKICK_SUCCESS)
                            {
                                session.Write(new PurchaseLotResponse()
                                {
                                    Status = PurchaseLotStatus.FAILED,
                                    Reason = PurchaseLotFailureReason.IN_LOT_CANT_EVICT
                                });
                                return;
                            }
                        }
                        /*
                        //we can't be in the lot when this happens. Make sure city owns avatar.
                        bool canEvict = session.AvatarClaimId != 0;
                        if (!canEvict)
                        {
                            var claim = db.AvatarClaims.Get(session.AvatarClaimId);
                            if (claim.owner == Context.Config.Call_Sign)
                            {
                                db.Roommates.RemoveRoommate(session.AvatarId, myLots[0].lot_id);
                                canEvict = true;
                            }
                            else canEvict = false;
                        }

                        if (!canEvict)
                        {
                            session.Write(new PurchaseLotResponse()
                            {
                                Status = PurchaseLotStatus.FAILED,
                                Reason = PurchaseLotFailureReason.IN_LOT_CANT_EVICT
                            });
                            return;
                        }
                        */
                    }

                    var name = packet.Name;
                    if (!GlobalRealestate.ValidateLotName(name))
                    {
                        session.Write(new PurchaseLotResponse()
                        {
                            Status = PurchaseLotStatus.FAILED,
                            Reason = PurchaseLotFailureReason.NAME_VALIDATION_ERROR
                        });
                        return;
                    }

                    var transactionResult = db.Avatars.Transaction(session.AvatarId, uint.MaxValue, price, 5); //expenses misc... maybe add specific for lot
                    resultFunds = transactionResult.source_budget;
                    if (!transactionResult.success)
                    {
                        session.Write(new PurchaseLotResponse()
                        {
                            Status = PurchaseLotStatus.FAILED,
                            Reason = PurchaseLotFailureReason.INSUFFICIENT_FUNDS,
                            NewFunds = resultFunds
                        });
                        return;
                    }

                    try
                    {
                        lotId = db.Lots.Create(new DbLot
                        {
                            name = name,
                            shard_id = Context.ShardId,

                            location = packedLocation,
                            owner_id = session.AvatarId,
                            created_date = Epoch.Now,
                            category_change_date = Epoch.Default,
                            category = LotCategory.none,

                            buildable_area = 1,
                            description = ""
                        });

                        DataService.Invalidate<FSO.Common.DataService.Model.Lot>(packedLocation);
                    }
                    catch (Exception ex)
                    {
                        var returnMoney = db.Avatars.Transaction(uint.MaxValue, session.AvatarId, price, 5); //refund
                        //Name taken
                        if (ex.Message == "NAME")
                        {
                            session.Write(new PurchaseLotResponse()
                            {
                                Status = PurchaseLotStatus.FAILED,
                                Reason = PurchaseLotFailureReason.NAME_TAKEN, //TODO: this can also happen if the location was taken. (location is a UNIQUE row)
                                NewFunds = returnMoney.dest_budget
                            });
                        }
                        else
                        {
                            session.Write(new PurchaseLotResponse()
                            {
                                Status = PurchaseLotStatus.FAILED,
                                Reason = PurchaseLotFailureReason.UNKNOWN, //likely already roommate somewhere else, or we got race condition'd by another roomie request
                                NewFunds = returnMoney.dest_budget
                            });
                        }
                        return;
                    }
                }

            }

            //lot init happens on first join, as part of the loading process. If the lot somehow crashes before first save, it'll just be a blank slate again.

            //TODO: Broadcast to the world a new lot exists. i think we do this?

            //Update my sim's lot
            var avatar = await DataService.Get<Avatar>(session.AvatarId);
            if (avatar != null) avatar.Avatar_LotGridXY = packedLocation;
            
            session.Write(new PurchaseLotResponse()
            {
                Status = PurchaseLotStatus.SUCCESS,
                NewLotId = lotId,
                NewFunds = resultFunds
            });
        }
    }
}
