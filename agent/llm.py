"""Minimal OpenAI-compatible chat client with provider fallback. Standard library only.

Configuration comes from environment variables (never put keys in code or in git):

    DEEPSEEK_API_KEY     DeepSeek key
    DASHSCOPE_API_KEY    Qwen (Alibaba Model Studio / 百炼) key. Keys are region-specific.
    LLM_ORDER            provider order, default "deepseek,qwen"
    DEEPSEEK_MODEL       default "deepseek-flash"
    DEEPSEEK_BASE_URL    default "https://api.deepseek.com"
    QWEN_MODEL           default "qwen-plus"
    QWEN_BASE_URL        default "https://dashscope.aliyuncs.com/compatible-mode/v1"
                         (Singapore keys: https://dashscope-intl.aliyuncs.com/compatible-mode/v1)
    LLM_TIMEOUT          seconds per provider attempt, default 12

Model names and endpoints change; the defaults were checked against the vendors'
docs on 2026-11-17. If a call fails with 400/404, check the model name first.
"""
import json
import os
import socket
import urllib.error
import urllib.request


class LLMError(Exception):
    def __init__(self, kind, detail):
        super().__init__(f"{kind}: {detail}")
        self.kind = kind        # timeout | network | http | bad_response | not_configured
        self.detail = detail


class Provider:
    def __init__(self, name, base_url, api_key, model, timeout=12.0):
        self.name, self.base_url, self.api_key, self.model, self.timeout = name, base_url.rstrip("/"), api_key, model, timeout

    def chat(self, messages, tools=None):
        """Returns the assistant message dict: {'content': str|None, 'tool_calls': [...]|None}."""
        body = {"model": self.model, "messages": messages, "temperature": 0.1, "max_tokens": 600, "stream": False}
        if tools:
            body["tools"] = tools
            body["tool_choice"] = "auto"
        req = urllib.request.Request(
            self.base_url + "/chat/completions",
            data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json", "Authorization": "Bearer " + self.api_key},
            method="POST")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
        except urllib.error.HTTPError as e:
            detail = ""
            try:
                detail = e.read().decode("utf-8", errors="replace")[:300]
            except Exception:
                pass
            raise LLMError("http", f"{self.name} HTTP {e.code} {detail}") from None
        except (socket.timeout, TimeoutError):
            raise LLMError("timeout", f"{self.name} no answer within {self.timeout}s") from None
        except urllib.error.URLError as e:
            if isinstance(e.reason, (socket.timeout, TimeoutError)):
                raise LLMError("timeout", f"{self.name} no answer within {self.timeout}s") from None
            raise LLMError("network", f"{self.name} {e.reason}") from None
        except OSError as e:
            raise LLMError("network", f"{self.name} {e}") from None
        try:
            message = json.loads(raw)["choices"][0]["message"]
            if not isinstance(message, dict):
                raise TypeError("message is not an object")
            return message
        except (ValueError, KeyError, IndexError, TypeError) as e:
            raise LLMError("bad_response", f"{self.name} unexpected body: {e}: {raw[:200]}") from None


class LLMChain:
    """Try each provider in order; raise the last error if all fail."""

    def __init__(self, providers):
        self.providers = list(providers)
        self.last_used = None

    def chat(self, messages, tools=None):
        if not self.providers:
            raise LLMError("not_configured", "no API key set (DEEPSEEK_API_KEY or DASHSCOPE_API_KEY)")
        last = None
        for provider in self.providers:
            try:
                message = provider.chat(messages, tools)
                self.last_used = provider.name
                return message
            except LLMError as e:
                last = e
        raise last


def chain_from_env(env=None):
    env = os.environ if env is None else env
    timeout = float(env.get("LLM_TIMEOUT", "12"))
    available = {}
    if env.get("DEEPSEEK_API_KEY"):
        available["deepseek"] = Provider(
            "deepseek", env.get("DEEPSEEK_BASE_URL", "https://api.deepseek.com"),
            env["DEEPSEEK_API_KEY"], env.get("DEEPSEEK_MODEL", "deepseek-flash"), timeout)
    if env.get("DASHSCOPE_API_KEY"):
        available["qwen"] = Provider(
            "qwen", env.get("QWEN_BASE_URL", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
            env["DASHSCOPE_API_KEY"], env.get("QWEN_MODEL", "qwen-plus"), timeout)
    order = [n.strip() for n in env.get("LLM_ORDER", "deepseek,qwen").split(",") if n.strip()]
    return LLMChain([available[n] for n in order if n in available])
