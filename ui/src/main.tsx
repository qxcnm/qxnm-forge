import "@fontsource-variable/inter";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import App from "@/App";
import { InterfaceProviders } from "@/components/interface-providers";
import { TooltipProvider } from "@/components/ui/tooltip";
import "@/i18n";
import "@/index.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
      staleTime: 30_000,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <InterfaceProviders>
        <TooltipProvider delayDuration={350}>
          <App />
        </TooltipProvider>
      </InterfaceProviders>
    </QueryClientProvider>
  </StrictMode>,
);
