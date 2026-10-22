import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { ApiService } from './api.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink],
  template: `
    <header class="topbar">
      <a class="brand" routerLink="/">
        <span class="brand-mark" aria-hidden="true"></span>
        <span>Agentic SDLC <b>Console</b></span>
      </a>
      <span class="spacer"></span>
      <a class="toplink" routerLink="/">New run</a>
    </header>
    @if (api.backendError(); as err) {
      <div class="banner banner-error" role="alert">
        <strong>Connection problem.</strong> {{ err }}
      </div>
    }
    <main class="container">
      <router-outlet />
    </main>
  `,
})
export class AppComponent {
  readonly api = inject(ApiService);
}
