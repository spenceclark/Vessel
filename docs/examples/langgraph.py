import contextvars
import uuid
from typing import Literal, cast

import httpx
from langchain.chat_models import init_chat_model
from langchain_core.callbacks import BaseCallbackHandler
from langchain_core.messages import AIMessage, HumanMessage, SystemMessage
from langchain_core.runnables import RunnableConfig
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.graph import END, START, MessagesState, StateGraph
from pydantic import BaseModel, Field

# Sample code taken from https://www.youtube.com/watch?v=uWLJAtMOVT0&t=213s

_vessel_headers: contextvars.ContextVar[dict | None] = contextvars.ContextVar(
    "vessel", default=None
)


class VesselCallback(BaseCallbackHandler):
    """Runs just before each LLM call, in the same context as the HTTP request that follows."""

    def on_chat_model_start(self, serialized, messages, *, metadata=None, **kwargs):
        md = metadata or {}
        headers = {}
        if node := md.get("langgraph_node"):  # 'classifier', 'chat_agent', ...
            headers["X-Vessel-Tags"] = node
        if thread := md.get("thread_id"):
            headers["X-Vessel-Session"] = str(thread)
        _vessel_headers.set(headers)


def _stamp_vessel(request: httpx.Request):
    request.headers.update(_vessel_headers.get() or {})


llm = init_chat_model(
    "openai:gpt-4o-mini",
    base_url="http://localhost:4550/v1",
    http_client=httpx.Client(event_hooks={"request": [_stamp_vessel]}),
)


class IntentClassifier(BaseModel):
    message_intent: Literal["chat", "knowledge", "code"] = Field(
        ...,
        description="Classify whether the user wants to just chat, ask for knowledge or change code in the project.",
    )


class State(MessagesState):
    message_intent: str | None


def classify_intent(state: State):

    structured_llm = llm.with_structured_output(IntentClassifier)

    raw_result = structured_llm.invoke(
        [
            SystemMessage(
                content='Determine / classify whether the user wants to chat ("chat"), ask for knowledge ("knowledge"), or change code ("code").'
            ),
            HumanMessage(content=state["messages"][-1].content),
        ]
    )

    result = cast(IntentClassifier, raw_result)

    return {"message_intent": result.message_intent}


def prompt_llm_chat(state: State):

    messages = [
        SystemMessage(content="You are a talkative chatbot for fun. Be nice.")
    ] + state["messages"]
    response = llm.invoke(messages)
    return {"messages": [AIMessage(content=response.content)]}


def prompt_llm_rag(state: State):

    messages = [
        SystemMessage(
            content='No matter what the user says, always say "I am the RAG man".'
        )
    ] + state["messages"]
    response = llm.invoke(messages)
    return {"messages": [AIMessage(content=response.content)]}


def prompt_llm_code(state: State):

    messages = [
        SystemMessage(
            content='No matter what the user says, always  say "I am the HACKER man".'
        )
    ] + state["messages"]
    response = llm.invoke(messages)
    return {"messages": [AIMessage(content=response.content)]}


graph_builder = StateGraph(State)

graph_builder.add_node("classifier", classify_intent)
graph_builder.add_node("chat_agent", prompt_llm_chat)
graph_builder.add_node("rag_agent", prompt_llm_rag)
graph_builder.add_node("code_agent", prompt_llm_code)

graph_builder.add_edge(START, "classifier")
graph_builder.add_conditional_edges(
    "classifier",
    lambda state: state["message_intent"],
    {"chat": "chat_agent", "knowledge": "rag_agent", "code": "code_agent"},
)
graph_builder.add_edge("chat_agent", END)
graph_builder.add_edge("rag_agent", END)
graph_builder.add_edge("code_agent", END)

checkpointer = InMemorySaver()

graph = graph_builder.compile(checkpointer=checkpointer)

thread_id = str(uuid.uuid4())
config: RunnableConfig = {
    "configurable": {"thread_id": thread_id},
    "metadata": {"thread_id": thread_id},
    "callbacks": [VesselCallback()],
}

while True:
    user_message = input("Enter message:")

    input_state: State = {
        "messages": [HumanMessage(content=user_message)],
        "message_intent": None,
    }

    result = graph.invoke(input_state, config=config)

    print(result["messages"][-1].content)
