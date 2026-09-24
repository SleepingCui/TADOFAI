

from __future__ import annotations

from fastapi import APIRouter, Request

from core.client_gateway import MAX_CLIENTS

router = APIRouter(tags=["state"])


@router.get("/api/state")
async def get_state(request: Request) -> dict:
    services = request.app.state.services
    return await services.state_store.as_wire()


@router.get("/api/clients")
async def get_clients(request: Request) -> dict:
    services = request.app.state.services
    subscribers = services.event_bus.subscriber_stats()
    return {
        "maxClients": MAX_CLIENTS,
        "count": len(subscribers),
        "droppedEvents": services.event_bus.total_dropped_events,
        "pendingEvents": services.event_bus.total_pending_events,
        "clients": subscribers,
    }
