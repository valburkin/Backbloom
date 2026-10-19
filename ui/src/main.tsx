import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './index.css';
import { createBackend } from './api/backend';
import { ConnectionProvider } from './app/connection';
import { EditorProvider } from './app/editor';
import { ToastProvider } from './app/toast';
import { App } from './app/App';

function Root() {
  const [backend] = useState(createBackend);
  return (
    <ToastProvider>
      <ConnectionProvider backend={backend}>
        <EditorProvider>
          <App />
        </EditorProvider>
      </ConnectionProvider>
    </ToastProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
