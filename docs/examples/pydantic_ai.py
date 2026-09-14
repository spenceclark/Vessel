"""Pydantic AI multi-agent flow tagged for Vessel: one agent delegates to another.

Every request carries X-Vessel-Tags (which agent sent it) and X-Vessel-Session (this run).
Cut down from the Pydantic AI flight-booking example.
"""

import asyncio
import datetime
import uuid

from pydantic import BaseModel
from pydantic_ai import Agent, RunContext
from pydantic_ai.models.openai import OpenAIChatModel
from pydantic_ai.providers.ollama import OllamaProvider

session_id = str(uuid.uuid4())


def vessel_headers(tag: str) -> dict:
    # Settings merge shallowly (model < agent < run), so a later extra_headers replaces an
    # earlier one wholesale. Always send the session and the tag together.
    return {"extra_headers": {"X-Vessel-Session": session_id, "X-Vessel-Tags": tag}}


model = OpenAIChatModel(
    "llama3.2",
    provider=OllamaProvider(base_url="http://127.0.0.1:4550/v1"),
)


class FlightDetails(BaseModel):
    flight_number: str
    price: int
    origin: str
    destination: str
    date: datetime.date


search_agent = Agent(
    model,
    model_settings=vessel_headers("search_agent"),
    deps_type=str,
    output_type=FlightDetails,
    system_prompt="Find the cheapest flight for the user on the given date.",
)

extraction_agent = Agent(
    model,
    model_settings=vessel_headers("extraction_agent"),
    output_type=list[FlightDetails],
    system_prompt="Extract all the flight details from the given text.",
)


@search_agent.tool
async def extract_flights(ctx: RunContext[str]) -> list[FlightDetails]:
    """Get details of all flights."""
    result = await extraction_agent.run(ctx.deps, usage=ctx.usage)
    return result.output


flights_web_page = """
1. Flight SFO-AK123 - $350 - SFO to ANC - January 10, 2025
2. Flight SFO-AK456 - $370 - SFO to FAI - January 10, 2025
3. Flight NYC-LA101 - $250 - SFO to ANC - January 10, 2025
4. Flight CHI-MIA202 - $200 - ORD to MIA - January 12, 2025
"""


async def main():
    result = await search_agent.run(
        "Find me a flight from SFO to ANC on 2025-01-10", deps=flights_web_page
    )
    print(result.output)


if __name__ == "__main__":
    asyncio.run(main())
